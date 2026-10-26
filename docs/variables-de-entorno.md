# Variables de entorno

Toda la configuración se entrega por variables de entorno; el repositorio no guarda valores ni secretos. Hay dos grupos:

- **Docker Compose** (`ERPSYNC_*`): las lee `docker-compose.yml` del entorno de tu sistema. El prefijo evita chocar con otros proyectos locales que usan nombres como `POSTGRES_PASSWORD`.
- **El hub y su configuración** (`Database__…`, `SAP__…`, `Seq__…`): las lee la aplicación. Dentro de los contenedores Compose las rellena a partir de las `ERPSYNC_*`; solo las defines tú cuando ejecutas el hub con `dotnet run`. En los nombres, `__` equivale a `:` en la configuración de .NET.

Cómo definirlas en cada sistema está en el [README](../README.md#variables-de-entorno).

## Docker Compose

| Variable | Obligatoria | Por defecto | Para qué sirve |
|---|---|---|---|
| `ERPSYNC_POSTGRES_PASSWORD` | Sí | — | Contraseña del usuario de PostgreSQL; el hub de Compose la usa para conectarse. |
| `ERPSYNC_WC_WEBHOOK_SECRET` | Sí | — | Secreto compartido del webhook de WooCommerce (mínimo 16 caracteres). Lo usan el hub, para verificar la firma, y `tienda-setup`, para registrar el webhook. |
| `ERPSYNC_POSTGRES_DB` | No | `erpsynchub` | Nombre de la base de datos. |
| `ERPSYNC_POSTGRES_USER` | No | `erpsynchub` | Usuario de PostgreSQL. |
| `ERPSYNC_POSTGRES_HOST_PORT` | No | `5433` | Puerto de PostgreSQL en el host (solo `127.0.0.1`). |
| `ERPSYNC_HUB_HOST_PORT` | No | `5200` | Puerto del hub en el host. |
| `ERPSYNC_SAP_MOCK_HOST_PORT` | No | `5201` | Puerto del SAP simulado en el host. |
| `ERPSYNC_SEQ_HOST_PORT` | No | `5202` | Puerto de la interfaz web de Seq en el host. |
| `ERPSYNC_TIENDA_HOST_PORT` | No | `5203` | Puerto de la tienda WooCommerce en el host (perfil `tienda`). |
| `ERPSYNC_SAP_COMPANY_DB` | No | `SBODemoCL` | Base de la compañía en SAP que usa el hub de Compose. |
| `ERPSYNC_SAP_USER_NAME` | No | `manager` | Usuario de SAP que usa el hub de Compose (credencial ficticia del mock). |
| `ERPSYNC_SAP_PASSWORD` | No | `manager` | Contraseña de SAP que usa el hub de Compose (credencial ficticia del mock). |
| `ERPSYNC_WP_DB_PASSWORD` | Con `--profile tienda` | — | Contraseña de MySQL de la tienda. |
| `ERPSYNC_WP_ADMIN_PASSWORD` | Con `--profile tienda` | — | Contraseña del administrador de WordPress que crea `tienda-setup`. |
| `ERPSYNC_WP_ADMIN_USER` | No | `admin` | Usuario administrador de WordPress. |
| `ERPSYNC_WP_ADMIN_EMAIL` | No | `admin@example.com` | Correo del administrador de WordPress. |
| `ERPSYNC_HUB_WEBHOOK_URL` | No | `http://hub:8080/webhooks/woocommerce/orders` | URL a la que WooCommerce envía los webhooks. Con el hub en el host (`dotnet run`, puerto 5210): `http://host.docker.internal:5210/webhooks/woocommerce/orders`. |
| `ERPSYNC_WC_CONSUMER_KEY` | Para sincronizar stock y precios | — | Clave de consumidor de la API REST de WooCommerce (`ck_` y 40 caracteres hexadecimales). Es la única fuente de verdad: `tienda-setup` la registra en WooCommerce y el hub la lee como `Store__WooCommerce__ConsumerKey`. Ver [credenciales de la API de WooCommerce](#credenciales-de-la-api-de-woocommerce). |
| `ERPSYNC_WC_CONSUMER_SECRET` | Para sincronizar stock y precios | — | Secreto de consumidor (`cs_` y 40 caracteres hexadecimales). Mismo uso que la clave; el hub lo lee como `Store__WooCommerce__ConsumerSecret`. |
| `ERPSYNC_WC_BASE_URL` | No | `http://tienda` si hay clave; si no, vacía | Dirección de la tienda a la que llama el hub. Solo hace falta para apuntar a otra tienda (por ejemplo, una real por HTTPS). |
| `ERPSYNC_INVENTORY_INTERVAL` | No | `00:15:00` | Cada cuánto corre la sincronización de stock y precios (`hh:mm:ss`). |
| `ERPSYNC_INVENTORY_WAREHOUSES` | No | `01` | Almacenes de SAP cuyo stock se publica, separados por comas (`01,02`). |
| `ERPSYNC_INVENTORY_PRICE_LIST` | No | `1` | Lista de precios de SAP de la que sale el precio. |
| `ERPSYNC_INVENTORY_PRICE_DECIMALS` | No | `0` | Decimales con que se envía el precio (cero para pesos chilenos). |
| `ERPSYNC_INVENTORY_FULL_INTERVAL` | No | `1.00:00:00` | Cada cuánto la sincronización periódica es completa, como respaldo (`d.hh:mm:ss`). `00:00:00` la desactiva. |
| `ERPSYNC_JOB_MAX_ATTEMPTS` | No | `12` | Intentos de un job antes de pasar al dead letter. |
| `ERPSYNC_JOB_RETRY_BASE_DELAY` | No | `00:00:30` | Espera tras el primer fallo; se duplica con cada intento. |
| `ERPSYNC_JOB_RETRY_MAX_DELAY` | No | `01:00:00` | Tope de la espera entre intentos. |
| `ERPSYNC_ADMIN_API_KEY` | Para usar el panel | — | Clave de acceso a `/admin/*` (cabecera `X-Api-Key`): de 32 a 256 caracteres sin espacios. Admite **varias claves separadas por coma** para rotarlas sin cortar el acceso. Sin ninguna, el panel responde 503. Ver [clave del panel](#clave-del-panel). |

Si un puerto del host está ocupado por otro proyecto, cambia la variable `*_HOST_PORT` que corresponda. Entre contenedores no importan: se hablan por nombre de servicio y puerto interno.

## El hub

Las mismas variables sirven para un despliegue fuera de Compose. Para el detalle de las del worker, el webhook y los códigos de error, ver [flujo de pedidos](flujo-de-pedidos.md#configuración).

| Variable | Obligatoria | Por defecto | Para qué sirve |
|---|---|---|---|
| `Database__ConnectionString` | Sí | — | Cadena de conexión a PostgreSQL. Con la base de Compose: `Host=localhost;Port=5433;Database=erpsynchub;Username=erpsynchub;Password=<ERPSYNC_POSTGRES_PASSWORD>`. |
| `Database__MigrateOnStartup` | No | `false` | Aplica las migraciones al iniciar (útil en desarrollo; Compose lo activa). |
| `Webhooks__WooCommerce__Secret` | Sí | — | Secreto del webhook; debe ser el mismo que el de WooCommerce. Mínimo 16 caracteres. |
| `SAP__BaseUrl` | Sí | — | URL del Service Layer. Con el SAP simulado de Compose: `http://localhost:5201`. |
| `SAP__CompanyDb` | Sí | — | Base de la compañía en SAP. |
| `SAP__UserName` | Sí | — | Usuario de SAP. |
| `SAP__Password` | Sí | — | Contraseña de SAP. Con un SAP real, entrégala desde un gestor de secretos. |
| `SAP__TaxIdFormat` | No | `WithDots` | Formato del RUT en `FederalTaxID`: `WithDots` (`12.345.678-5`) o `WithoutDots` (`12345678-5`). Debe coincidir con cómo lo guarda tu SAP: la búsqueda es estricta. |
| `Seq__ServerUrl` | No | — | URL de Seq (por ejemplo `http://localhost:5202`). Sin ella, los logs solo van a la consola. |
| `Seq__ApiKey` | No | — | Clave de API de Seq, si el servidor la exige. |
| `Store__WooCommerce__BaseUrl` | Con la tienda | — | Dirección de la tienda (`https://tienda.example.com`, sin `/wp-json`). Si no defines ninguna variable `Store__WooCommerce__*`, la tienda se considera no configurada y la sincronización de stock y precios queda desactivada (el hub lo avisa en el log al arrancar). Si defines alguna, las tres obligatorias deben estar. |
| `Store__WooCommerce__ConsumerKey` | Con la tienda | — | Clave de consumidor de la API REST: `ck_` y 40 caracteres hexadecimales. Debe tener permiso de lectura y escritura. |
| `Store__WooCommerce__ConsumerSecret` | Con la tienda | — | Secreto de consumidor: `cs_` y 40 caracteres hexadecimales. |
| `Store__WooCommerce__BatchSize` | No | `100` | Productos por lote al actualizar; entre 1 y 100, que es el límite de WooCommerce. |
| `Store__WooCommerce__RequestTimeout` | No | `00:00:30` | Tiempo máximo de cada petición a la tienda. |
| `Sync__Inventory__Enabled` | No | `true` | Apaga la sincronización periódica de stock y precios. La manual del panel no depende de este valor. |
| `Sync__Inventory__Interval` | No | `00:15:00` | Cada cuánto corre; entre 1 segundo y 24 horas. |
| `Sync__Inventory__FullInterval` | No | `1.00:00:00` | Cada cuánto la sincronización periódica es completa (lee todo el maestro y envía todo, ignorando la última foto), como respaldo por si `UpdateDate` de SAP no refleja algún movimiento de stock. Se mide desde la última completa exitosa, también la manual. `00:00:00` la desactiva; si no, debe ser al menos `Sync__Inventory__Interval` y no pasar de 30 días. |
| `Worker__Retry__MaxAttempts` | No | `12` | Intentos de un job, contando el primero, antes de pasar al dead letter (1 a 100). Un reintento manual concede un presupuesto nuevo. |
| `Worker__Retry__BaseDelay` | No | `00:00:30` | Espera tras el primer fallo transitorio; se duplica con cada intento (de 10 ms a 1 día). |
| `Worker__Retry__MaxDelay` | No | `01:00:00` | Tope de la espera entre intentos: al menos `BaseDelay` y como máximo 1 día. A cada espera se le aplica *jitter*: dura entre el 50 % y el 100 % de lo calculado. |
| `Admin__ApiKey` | Para usar el panel | — | Una o más claves de acceso a `/admin/*`, separadas por coma, de 32 a 256 caracteres sin espacios cada una. Se compara cada clave presentada con todas las configuradas en tiempo constante. Sin ninguna, el hub funciona (webhook, worker, `/health`) pero el panel responde 503 y el log lo avisa al arrancar. |
| `Sync__Inventory__Warehouses` | No | `01` | Códigos de los almacenes de SAP que se publican, separados por comas. El stock enviado es la suma de *en mano menos comprometido* de esos almacenes, con tope en 0. |
| `Sync__Inventory__PriceList` | No | `1` | Número de la lista de precios de SAP. Un precio nulo o menor o igual a 0 no se envía. |
| `Sync__Inventory__PriceDecimals` | No | `0` | Decimales del precio enviado (0 a 4). |
| `Sync__Inventory__SapPageSize` | No | `100` | Artículos que se leen de SAP por página (1 a 1000). |
| `Sync__Inventory__TimeZoneId` | No | `America/Santiago` | Zona horaria de SAP (IANA), para convertir el instante de una ejecución en la fecha local de SAP con que se filtra `UpdateDate`. |

La sincronización de stock y precios está explicada en [docs/flujo-de-stock-y-precios.md](flujo-de-stock-y-precios.md).

## Clave del panel

Todo lo que cuelga de `/admin` (jobs, reintentos, métricas, sincronización de inventario) exige la cabecera `X-Api-Key` con una de las claves de `ERPSYNC_ADMIN_API_KEY` (en el hub, `Admin__ApiKey`). El webhook de WooCommerce no la usa: se protege con su firma. `/health` y la documentación en desarrollo tampoco la piden.

- **Sin clave configurada** el panel no queda abierto: responde 503 con un mensaje que dice qué variable definir.
- **La clave nunca se registra**: ni la que presenta un cliente ni las configuradas aparecen en los logs; un acceso rechazado queda como advertencia con el método, la ruta y la IP.
- **La comparación es en tiempo constante** (SHA-256 de cada clave y `FixedTimeEquals`, recorriendo todas), así que no filtra por el tiempo de respuesta ni el largo ni en qué posición difieren.

Para generar una clave aleatoria de 64 caracteres hexadecimales y guardarla a nivel de usuario en Windows (PowerShell):

```powershell
function New-Hex([int]$bytes) {
    $buffer = [byte[]]::new($bytes)
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

[Environment]::SetEnvironmentVariable('ERPSYNC_ADMIN_API_KEY', (New-Hex 32), 'User')
```

En Linux y macOS: `export ERPSYNC_ADMIN_API_KEY="$(openssl rand -hex 32)"`. Abre una terminal nueva después de definirla y vuelve a levantar el hub (`docker compose up -d hub`) para que la lea. Para usarla: `Invoke-RestMethod http://localhost:5200/admin/metrics -Headers @{ 'X-Api-Key' = $env:ERPSYNC_ADMIN_API_KEY }`.

### Rotar la clave sin cortar el acceso

El hub lee la clave al arrancar, así que cambiarla exige reiniciarlo; admitir varias a la vez permite hacerlo sin que ningún cliente (scripts, paneles, el monitoreo) quede sin acceso en ningún momento:

1. **Genera la clave nueva** (`B`) como arriba, sin tocar la vigente (`A`).
2. **Acepta las dos**: define `ERPSYNC_ADMIN_API_KEY=A,B` (la clave vigente primero, separadas por coma y sin espacios) y reinicia el hub. Desde aquí `A` y `B` abren el panel.
3. **Pasa los clientes a `B`**: actualiza cada script, panel o monitor que use la clave. Comprueba que responden bien con `B`.
4. **Confirma que nadie usa `A`**: en los logs (Seq) no deberían aparecer advertencias «Acceso rechazado al panel» de clientes legítimos, que son los que fallarían al retirar `A`.
5. **Retira la vieja**: define `ERPSYNC_ADMIN_API_KEY=B` y reinicia el hub. `A` deja de valer.

Si una clave se filtra, sáltate los pasos 3 y 4: define solo la clave nueva (`ERPSYNC_ADMIN_API_KEY=B`) y reinicia de inmediato, y luego actualiza los clientes.

## Credenciales de la API de WooCommerce

El hub necesita una clave de la API REST de WooCommerce con permiso de lectura y escritura. Las variables son la única fuente de verdad: no se generan al levantar el entorno ni se crean a mano en el administrador. Con `--profile tienda`, `tienda-setup` registra esa clave en WooCommerce de forma idempotente (guardando el hash como lo hace WooCommerce), y el hub la usa para llamar a la API.

Para generarlas con valores aleatorios criptográficamente seguros y guardarlas a nivel de usuario en Windows (PowerShell):

```powershell
function New-Hex([int]$bytes) {
    $buffer = [byte[]]::new($bytes)
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

[Environment]::SetEnvironmentVariable('ERPSYNC_WC_CONSUMER_KEY', "ck_$(New-Hex 20)", 'User')
[Environment]::SetEnvironmentVariable('ERPSYNC_WC_CONSUMER_SECRET', "cs_$(New-Hex 20)", 'User')
```

Abre una terminal nueva después de ejecutarlo. Para revisarlas: `[Environment]::GetEnvironmentVariable('ERPSYNC_WC_CONSUMER_KEY', 'User')`; para borrarlas, el mismo comando con `$null`. Si las cambias, la clave nueva se registra al volver a ejecutar `tienda-setup` (`docker compose --profile tienda up -d`), pero la anterior sigue registrada y válida hasta que la borres en *WooCommerce → Ajustes → Avanzado → API REST*.

En Linux y macOS: `export ERPSYNC_WC_CONSUMER_KEY="ck_$(openssl rand -hex 20)"` y `export ERPSYNC_WC_CONSUMER_SECRET="cs_$(openssl rand -hex 20)"`.

**Solo HTTPS en una tienda real.** WooCommerce acepta la clave y el secreto como autenticación Basic únicamente por HTTPS. El entorno local de Compose usa HTTP por la red interna y un *mu-plugin* de desarrollo (`docker/tienda/mu-plugins/permitir-api-rest-por-http.php`) lo permite; en producción la tienda debe servirse por HTTPS.

## Seguridad

- Los valores por defecto de SAP son las credenciales ficticias del SAP simulado; no son secretos.
- Seq se levanta sin autenticación y solo es apto para desarrollo local.
- Nunca guardes contraseñas ni secretos reales en el repositorio, en un `.env` versionado ni en la imagen.
