# 0003. PostgreSQL como cola de jobs

- **Estado:** Aceptada
- **Fecha:** 2026-10-22

## Contexto

Los pedidos llegan por webhook y deben procesarse contra SAP de forma asíncrona, sin perderse aunque SAP esté caído o el proceso se reinicie. Hace falta persistir cada trabajo, garantizar que dos workers no tomen el mismo, reintentar con backoff y conservar los que fallan definitivamente (*dead letter*) para inspeccionarlos y reintentarlos a mano.

El hub ya necesita PostgreSQL para sus datos. Un broker de mensajes (RabbitMQ, Azure Service Bus) añadiría otro componente que desplegar, monitorear y mantener consistente con la base de datos.

## Decisión

La cola es una tabla de PostgreSQL (`sync_jobs`). Los workers toman trabajos con `SELECT ... FOR UPDATE SKIP LOCKED`, de modo que varios workers consumen en paralelo sin bloquearse ni procesar el mismo job dos veces.

- Registrar el job y el cambio de estado ocurren en la **misma transacción** que el resto de los datos, sin el problema de escritura doble entre base de datos y broker.
- El estado del job (pendiente, en proceso, completado, reintento programado, *dead letter*), el número de intentos y el último error son columnas consultables, lo que sirve directamente al panel.
- La idempotencia se apoya en una clave única por origen del evento (por ejemplo, el identificador del pedido de la tienda).

## Consecuencias

- Un componente menos que operar: basta PostgreSQL, y el entorno de pruebas usa el mismo motor con Testcontainers.
- Visibilidad total de la cola con SQL estándar.
- El rendimiento está acotado por PostgreSQL; para el volumen de una tienda es más que suficiente. Si el volumen creciera de forma importante, la cola puede extraerse detrás de una interfaz de `Application` sin afectar a los casos de uso.
- Hay que cuidar el sondeo (intervalo de *polling*) y el índice sobre estado y fecha de próxima ejecución para no sobrecargar la base de datos.
