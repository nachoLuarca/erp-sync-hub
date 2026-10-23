# 0007. Inbox y cola con lease para los pedidos de la tienda

- **Estado:** Aceptada
- **Fecha:** 2026-10-23

## Contexto

WooCommerce envía un webhook cuando un pedido se crea o cambia. La tienda espera una respuesta rápida: si tarda o falla, reintenta varias veces y finalmente desactiva el webhook. Crear la orden en SAP, en cambio, puede tardar (inicio de sesión, varias consultas) y SAP puede estar caído. Procesar el pedido dentro de la solicitud HTTP acoplaría la disponibilidad de la tienda a la de SAP, y se perdería el pedido si el proceso muriera a mitad de camino.

Además, la tienda reenvía el mismo pedido muchas veces: lo envía al crearlo y de nuevo cada vez que cambia de estado (`processing` → `completed`), y lo reintenta si no recibió nuestra respuesta.

El [ADR 0003](0003-postgresql-como-cola.md) decidió usar PostgreSQL como cola; este documento fija cómo.

## Decisión

### Recepción (inbox)

El endpoint `POST /webhooks/woocommerce/orders` solo **verifica y guarda**; no habla con SAP.

1. Responde `200` al *ping* de creación del webhook (cuerpo `webhook_id=N`, que no es JSON).
2. Verifica la firma `X-WC-Webhook-Signature` (HMAC-SHA256 en Base64 sobre el cuerpo crudo, comparación en tiempo constante) **antes** de interpretar el cuerpo. Firma inválida: `401` con ProblemDetails.
3. Ignora con `200` los topics que no traen un pedido completo (`order.deleted`, `order.restored`) y los pedidos que no están pagados: solo se encolan los estados `processing` y `completed` (configurable). Un pedido `pending` crearía una orden de venta en SAP por algo que aún no se vendió.
4. Guarda el pedido en `sync_jobs` con `INSERT ... ON CONFLICT DO NOTHING` y responde `202`.

La **idempotencia** la da el índice único `(source, external_id, type)`. Un pedido que llega otra vez, por ejemplo al pasar de `processing` a `completed`, se responde igual con `202` (la tienda solo necesita un 2xx) y no se reprocesa. Los pedidos se guardan con su cuerpo original (`jsonb`) y el total de la tienda.

### Cola con `FOR UPDATE SKIP LOCKED` y lease

Los workers toman trabajo en una transacción corta: `SELECT ... FOR UPDATE SKIP LOCKED` de los jobs pendientes **y** de los que tienen el lease vencido, más antiguos primero. El job pasa a `processing`, queda asignado al worker hasta `locked_until` y se abre un registro en `sync_attempts`. El procesamiento (llamadas a SAP) ocurre fuera de esa transacción, que se cierra en otra aparte.

- **Un job por claim.** Cada bucle toma uno, lo procesa y vuelve a tomar; reclamar lotes arriesga que los últimos jobs venzan su lease esperando turno.
- **Lease mayor que el tiempo de espera de SAP**, validado al iniciar. El procesamiento de un job tiene además un **tiempo máximo menor que el lease** que impone el propio worker: un job nunca sigue corriendo cuando otro worker ya pudo retomarlo.
- **Un worker que muere** deja un intento *en progreso*; cuando el lease vence, otro worker retoma el job y marca el intento anterior como *abandonado*.
- **`xmin` como token de concurrencia.** Cerrar un job ocurre en otra transacción; si un worker lento intenta cerrar un job que otro ya retomó, la escritura falla (`job.lost_lease`) y no pisa el estado.
- **Resultado con token propio.** El cierre del job usa un token independiente del apagado del servicio: si el servicio se detiene justo después de crear la orden en SAP, el resultado no se pierde. Si igualmente no se pudiera escribir, el job se reprocesa y, como cada paso es idempotente ([ADR 0008](0008-sincronizacion-de-pedidos.md)), no se duplica nada.
- **Apagado ordenado.** Al detenerse, el worker **libera** el job en curso (vuelve a `pending`, intento abandonado) en lugar de dejarlo esperando a que venza el lease.

### Fallos

En esta fase, un fallo solo registra el intento (código, mensaje y clasificación del error) y deja el job en `failed`. El backoff exponencial, el *dead letter* y el reintento manual llegan en la Fase 4; el modelo ya distingue errores definitivos (`rejected`) de pasajeros (`transient`) para decidirlo.

### Salud

`/health` informa el estado de PostgreSQL y de SAP. PostgreSQL caído, o sin migrar, es `Unhealthy` (503): sin base de datos el hub no puede guardar nada. SAP caído solo **degrada** (200): el inbox sigue recibiendo pedidos mientras SAP está caído, que es justamente la razón de este diseño, y un orquestador no debería reiniciar el servicio por eso.

## Consecuencias

- La tienda recibe una respuesta rápida y estable aunque SAP esté caído; ningún pedido pagado se pierde mientras PostgreSQL esté disponible.
- El procesamiento es *at-least-once*: un job puede ejecutarse más de una vez (worker caído, apagado, cierre fallido). Es seguro porque cada paso hacia SAP se puede repetir sin efectos duplicados.
- Varios workers (y varias instancias del hub) pueden consumir la misma cola sin coordinarse, y ninguno espera a otro.
- Los jobs terminados se acumulan en `sync_jobs`; los índices parciales mantienen rápida la cola, pero habrá que definir una política de retención.
- Un pedido que se cancela en la tienda después de sincronizado no se anula en SAP: las cancelaciones no están en el alcance de esta fase.
- Los pedidos se guardan con los datos personales del cliente (payload completo): la base de datos debe protegerse como tal.
