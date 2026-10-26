# 0009. Sincronización de stock y precios: lectura incremental, foto sincronizada y exclusión mutua

- **Estado:** Aceptada
- **Fecha:** 2026-10-26

## Contexto

El stock y los precios viven en SAP y deben reflejarse en la tienda WooCommerce. Es una sincronización periódica, no por eventos: SAP Business One no avisa cuando cambia un artículo. El catálogo puede tener decenas de miles de artículos, la tienda limita las escrituras por lote, y el hub puede correr en varias instancias a la vez. Hay que evitar tres costos: leer todo el maestro en cada pasada, escribir en la tienda lo que no cambió, y que dos ejecuciones se pisen.

## Decisión

### Lectura incremental y paginada desde SAP

- Se lee `Items` ordenado por `ItemCode`, con `$select` de lo necesario (stock por almacén, precios, estado y fechas), y se pagina **por clave**: cada página pide `ItemCode gt '<último recibido>'` con `$top`. A diferencia de `$skip`, no salta ni repite artículos si el maestro cambia mientras se lee, y puede reanudarse.
- El filtro incremental es `UpdateDate ge '<fecha de corte>'`. El Service Layer expone `UpdateDate` (fecha) y `UpdateTime` (hora), **sin zona horaria**. Por eso la fecha de corte es **el día local de SAP en que empezó la última ejecución sin errores, menos un día** (`Sync:Inventory:TimeZoneId`, `America/Santiago` por defecto). El día de traslape absorbe las diferencias de reloj y de zona; el costo es releer algo de más, que la foto descarta.
- Sin ejecución previa, o con `full=true`, se lee todo el maestro.
- **Supuesto a validar con un SAP real:** que `UpdateDate` se actualice también cuando cambian las existencias (movimientos de inventario) y no solo cuando se edita el maestro. El SAP simulado lo hace así. Si el SAP real no lo hiciera, el stock podría quedar desfasado hasta una lectura completa; la salida es disparar `full=true` periódicamente.

### Solo se envía lo que cambió: la foto sincronizada

Se guarda, **por SKU**, el stock y el precio que la tienda **confirmó** (`item_snapshots`). Cada artículo leído se compara contra su foto y solo se envía la diferencia (stock, precio o ambos). Se compara contra la foto y no contra la tienda porque consultar la tienda por cada artículo costaría una petición por producto.

- La foto es por SKU y no por `ItemCode` porque la tienda se identifica por SKU y varios SKU pueden apuntar al mismo artículo.
- **La foto solo se actualiza con lo que la tienda confirmó.** Si un producto falla, su foto no cambia y la siguiente ejecución lo reintenta sola.
- Una ejecución con errores no avanza la fecha de corte (solo cuentan las ejecuciones `succeeded`): lo que falló sigue dentro de la ventana de lectura.
- Si alguien edita stock o precio directamente en la tienda, la foto no se entera: el valor de SAP no cambió, así que no se reenvía. Se corrige con una sincronización completa (`full=true`), que ignora la foto.

### Qué se publica

- **Stock disponible** = suma de (*en mano* − *comprometido*) de los almacenes configurados (`Sync:Inventory:Warehouses`), con **tope en 0** y redondeado hacia abajo. Se suma antes de aplicar el tope: el sobrecompromiso de un almacén resta del disponible total. Un artículo sin los almacenes configurados queda en 0.
- Un artículo **inhabilitado o congelado** en SAP queda con stock 0 pero **no se despublica**: ocultar productos es una decisión comercial, no de inventario.
- **Precio** = el de la lista de precios configurada (`Sync:Inventory:PriceList`), redondeado a `Sync:Inventory:PriceDecimals` decimales (0 por defecto). Un precio nulo o menor o igual a 0 **no se envía** y queda en una advertencia agrupada por ejecución; el stock del artículo sí se sincroniza. Se escribe el precio *regular*: un precio de oferta de la tienda no se toca.

### Resolución de SKU

Igual que al sincronizar pedidos ([ADR 0008](0008-sincronizacion-de-pedidos.md)): se usan los mapeos guardados en `product_mappings` (un artículo puede tener varios SKU) y, sin mapeo, se asume que **el `ItemCode` es el SKU**. Si la tienda tiene un producto con ese SKU, se guarda el mapeo. **El hub no crea productos**: un artículo sin producto en la tienda se **omite**, se cuenta como tal y se avisa en una sola advertencia por ejecución con los primeros SKU.

### Escritura en WooCommerce

- Productos por SKU: `GET wc/v3/products?sku=A,B,…` en tandas de 100. Un SKU con coma no se puede buscar (la lista se separa por comas).
- Actualización: `POST wc/v3/products/batch`, en lotes de **hasta 100** (el límite por defecto de WooCommerce, `Store:WooCommerce:BatchSize`). Con stock se envía también `manage_stock: true`; sin él, WooCommerce ignora la cantidad.
- Un error por producto no impide actualizar a los demás. Credenciales rechazadas (401/403) interrumpen la ejecución. Una falla pasajera (5xx, red, tiempo agotado) corta los lotes siguientes y la ejecución termina como fallida; el siguiente intervalo reintenta. El backoff exponencial y el dead letter llegan en la Fase 4.
- **Autenticación:** clave y secreto de consumidor por Basic, que WooCommerce solo acepta por **HTTPS**. No se implementó OAuth 1.0a (el mecanismo de WooCommerce para HTTP) porque exige que la dirección con que se firma coincida con la URL del sitio, y en producción la tienda es HTTPS. Para el entorno local de Compose (HTTP por la red interna), un *mu-plugin* de desarrollo hace que WordPress trate como seguras las peticiones a la API REST.
- Las credenciales vienen solo de variables de entorno (`Store__WooCommerce__*`), con formato `ck_`/`cs_` y 40 caracteres hexadecimales validado al iniciar. `tienda-setup` las registra en WooCommerce de forma idempotente.
- Solo se actualizan **productos simples**: las variaciones de un producto variable tienen su propio SKU pero no aparecen en la búsqueda de productos.

### Exclusión mutua entre instancias

Nunca corren dos sincronizaciones a la vez, aunque haya varias instancias del hub. Se usa un ***advisory lock* de PostgreSQL** (`pg_try_advisory_lock`) tomado **sin esperar**: si otra ejecución lo tiene, la nueva se omite y lo registra.

- El lock vive en una **conexión dedicada, sin pool**, durante toda la ejecución. Si la instancia muere, la base cierra la sesión y suelta el lock sola: una caída nunca deja la sincronización bloqueada. Un lock por tabla con *lease* habría exigido renovar y expirar a mano.
- Con el lock tomado, las ejecuciones que figuren *en curso* son de una instancia que cayó: se cierran como `abandoned`.
- La sincronización manual usa el mismo lock: si hay una en curso, responde 409.

### Registro y correlación

Cada ejecución queda en `inventory_sync_runs` con su disparador (`scheduled` o `manual`), su estado (`succeeded`, `completed_with_errors`, `failed`, `abandoned`), la fecha de corte usada y los contadores. Cada artículo leído cae en exactamente una categoría: **actualizado**, **sin cambios**, **omitido** (sin producto en la tienda o sin precio válido) o **con error**. Todos los logs de la ejecución llevan la propiedad `InventoryRunId` (el `id` del registro), de modo que en Seq `InventoryRunId = '…'` muestra la ejecución completa.

### Disparo y configuración

El `InventorySyncWorker` corre al iniciar y luego cada `Sync:Inventory:Interval` (15 minutos por defecto). Queda **desactivado si la tienda no está configurada**, y lo dice con una advertencia al arrancar. El panel ofrece `POST /admin/sync/inventory` (con `full=true` opcional), `GET /admin/sync/inventory/last` y `GET /admin/sync/inventory/runs`.

## Consecuencias

- Cada pasada cuesta una lectura de SAP proporcional a lo que cambió y escrituras solo de lo diferente.
- Una pasada interrumpida o fallida se recupera sola: la foto y la fecha de corte avanzan únicamente con lo confirmado.
- La foto puede quedar desfasada si alguien edita la tienda a mano; `full=true` es la válvula.
- Los endpoints del panel **no tienen autenticación todavía**; se resuelve con el panel de la Fase 4. Hasta entonces, el hub no debe quedar expuesto fuera de la red de confianza.
- Productos variables y SKU con coma quedan fuera de alcance.
- El supuesto sobre `UpdateDate` y el formato de fechas del Service Layer real deben validarse contra un SAP real.
