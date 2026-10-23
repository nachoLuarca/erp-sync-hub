# 0008. Sincronización de pedidos: idempotencia, clientes, productos y precios

- **Estado:** Aceptada
- **Fecha:** 2026-10-23

## Contexto

Un pedido de WooCommerce debe convertirse en una orden de venta de SAP Business One. Como el procesamiento es *at-least-once* ([ADR 0007](0007-inbox-y-cola-con-lease.md)), cada paso debe poder repetirse sin crear duplicados. Además, tienda y ERP identifican de forma distinta a los clientes y los productos, y calculan los totales de forma distinta.

## Decisión

### Orden de los pasos

1. **Buscar la orden por su referencia.** La orden se crea con `NumAtCard = WC-{id del pedido}`. Si ya existe una con esa referencia, el job termina con éxito sin crear nada más.
2. **Resolver los productos.** Antes que el cliente: un SKU inválido no debe dejar un cliente nuevo en SAP para un pedido que va a fallar.
3. **Resolver el cliente.**
4. **Crear la orden.**

Las validaciones que no necesitan SAP (sin líneas, SKU vacío, cantidad inválida) se hacen antes de la primera llamada.

### Productos

1. Se busca el SKU en `product_mappings`.
2. Si no hay mapeo, se asume que **el SKU es el `ItemCode`**, pero solo si SAP lo conoce y está activo; en ese caso se guarda el mapeo para no volver a consultarlo.
3. Si no existe, el job falla de forma definitiva con `order.unknown_sku`.

### Clientes

La identidad del cliente en la tienda es su id (cliente registrado) o su correo normalizado (invitado). El orden de resolución es:

1. Mapeo guardado en `customer_mappings` (`customer:4521` o `email:ana@example.com`).
2. **RUT.** Si el pedido trae el RUT (metadato `Sync:WooCommerce:TaxIdMetaKey`, por defecto `_billing_rut`), se valida con el **dígito verificador (módulo 11)** antes de buscar o crear nada. Se busca en SAP por `FederalTaxID` (`LicTradNum`), entre los clientes (`cCustomer`), en sus dos formas habituales: con puntos (`12.345.678-5`) y sin ellos (`12345678-5`). Si hubiera varios, se elige el de menor `CardCode`, para que el resultado sea determinista.
3. **Correo**, si el pedido no trae RUT.
4. Si no existe, **se crea** con un `CardCode` determinista.

Un RUT presente pero con dígito verificador incorrecto **detiene el pedido** (`order.invalid_tax_id`, definitivo): crear al cliente sin RUT dejaría un problema tributario silencioso, y es preferible que una persona corrija el dato en la tienda. Un cliente encontrado pero inhabilitado o congelado en SAP también es un error definitivo (`order.inactive_customer`). Con un RUT que SAP no conoce no se "adopta" a otro cliente por coincidencia de correo.

#### Cómo se genera el `CardCode` de un cliente nuevo

El `CardCode` es **determinista**: el mismo cliente produce siempre el mismo código. Así, crear el cliente dos veces (por ejemplo, al reanudar un job que se cortó a medias) hace que SAP rechace el duplicado, y el adaptador lo interpreta como "ya existe" y reutiliza al existente. Todos los códigos caben en los 15 caracteres de `CardCode`.

| Situación | Código | Ejemplo | Largo máximo |
|---|---|---|---|
| El pedido trae un RUT válido | `C` + RUT sin puntos | `C12345678-5` | 11 |
| Cliente registrado sin RUT | `WC` + id del cliente en la tienda | `WC4521` | 15 (ids hasta 9.999.999.999.999) |
| Invitado sin RUT | `WG` + 12 caracteres hexadecimales del SHA-256 del correo en minúsculas | `WG8E43CA377012` | 14 |

- Se descartó la numeración automática de SAP (serie `C00007`): no es determinista, y obligaría a depender de la búsqueda por RUT o correo para evitar duplicados.
- Dos RUT distintos nunca colisionan. En el caso del invitado, 48 bits de hash hacen despreciable la colisión entre correos distintos, y un correo cambiado produce un cliente distinto, que es lo esperable.
- El código no es secuencial ni informa el orden de alta; para eso están los campos de SAP. Un cliente existente en SAP (encontrado por RUT o correo) conserva su código original.
- El RUT se guarda en `FederalTaxID` **con puntos**. Hay que validar con un SAP real que el campo y el formato coincidan con la configuración local.

### Precio y totales

- **Precio unitario = total de la línea ÷ cantidad**, redondeado a 4 decimales. El total de línea de WooCommerce ya descuenta los cupones y excluye impuestos, de modo que el precio es el que efectivamente pagó el cliente por unidad.
- **Envío e impuestos quedan fuera de la orden** en esta fase; el total de SAP es la suma de las líneas.
- El job guarda `store_total` (total del pedido en la tienda, con envío e impuestos) y `sap_total`. Cuando difieren se registra una **advertencia** que incluye el desglose (impuestos, envío y descuentos de la tienda), para distinguir una diferencia esperada de un error real. La diferencia es esperada en todo pedido con envío o IVA.
- La orden se fecha con el día de la compra en la zona horaria de la tienda (`America/Santiago` por defecto), no el día UTC: una compra a las 23:30 en Chile ya es "mañana" en UTC.

### Clasificación de los errores

| Origen | Clasificación | Ejemplos |
|---|---|---|
| Datos del pedido o del maestro de SAP | Definitivo (`rejected`) | `order.unknown_sku`, `order.invalid_tax_id`, `order.inactive_customer`, `sap.rejected` (4xx) |
| Indisponibilidad de SAP | Pasajero (`transient`) | `sap.server_error` (5xx, 408, 425, 429), `sap.session`, `sap.unreachable`, `sap.timeout`, `job.timeout` |
| Defecto inesperado | `unexpected` | `job.unexpected` |

## Consecuencias

- Reprocesar un job, por cualquier causa, no duplica clientes ni órdenes.
- Un pedido con un dato inválido (SKU, RUT) queda *fallido* con un mensaje en español que dice qué corregir, en lugar de crear datos incompletos en SAP.
- Los totales de SAP y de la tienda difieren mientras el envío y el IVA estén fuera de alcance; la advertencia lo deja visible. Incorporarlos (envío como artículo de servicio, IVA con el código de impuesto de SAP) es trabajo futuro.
- La búsqueda por correo asume que el correo identifica a un cliente; si SAP guarda el correo con otra capitalización, no lo encontrará (el hub guarda los correos en minúsculas).
- Las equivalencias se pueden precargar en `product_mappings` y `customer_mappings` (por ejemplo, para SKU distintos de los `ItemCode`); la sincronización de catálogo de la Fase 3 las poblará.
