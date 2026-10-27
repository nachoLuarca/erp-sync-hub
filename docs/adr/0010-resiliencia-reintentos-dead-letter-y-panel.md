# 0010. Resiliencia: reintentos con backoff, dead letter, panel autenticado y datos personales

- **Estado:** Aceptada
- **Fecha:** 2026-10-27

## Contexto

Hasta la Fase 3 un job que fallaba quedaba `failed` para siempre, aunque la causa fuera una caída de SAP de diez minutos. La integración tiene que sobrevivir a caídas y mantenciones de SAP de varias horas sin perder pedidos, sin duplicar órdenes y sin que nadie tenga que mirar la base de datos; y cuando algo no se pueda resolver solo (un SKU sin mapeo, un RUT inválido), tiene que quedar a la vista de una persona con una forma segura de corregirlo y reintentar. El panel que permite eso expone datos personales de clientes y acciones que modifican el estado, así que no puede estar abierto.

## Decisión

### Qué se reintenta y qué no

La clasificación ya existía en `ResultError` y los adaptadores de SAP y de WooCommerce la rellenan. `RetryPolicy` solo la lee:

| Clasificación | Ejemplos | Qué pasa |
|---|---|---|
| `Transient` | SAP caído, tiempo agotado, 5xx, sesión, red, `job.timeout` | Se reintenta con backoff hasta agotar los intentos. |
| `Unexpected` | Una excepción no prevista al procesar el job | **Se reintenta igual**: puede ser pasajera (una base de datos que parpadea) y, si es un defecto, el límite de intentos la lleva al dead letter, donde queda a la vista. Cada intento se registra con nivel `Error` y la excepción completa. |
| `Rejected`, `Validation`, `NotFound`, `Conflict` | SKU sin mapeo, RUT inválido, cliente inhabilitado, SAP respondió 4xx | **No se reintenta**: repetirlo no cambiaría el resultado. El job queda `failed`. |

Un corte de conexión a mitad de la respuesta (`IOException`) se traduce a falla de red transitoria (`sap.unreachable`, `store.unreachable`) en los dos adaptadores, en vez de escapar como inesperado.

### Estados

| Estado | Terminal | Significado |
|---|---|---|
| `pending` | no | Esperando a un worker. |
| `processing` | no | Tomado por un worker, mientras dure su lease. |
| `retrying` | no | Falló por un error transitorio y espera su próximo intento (`next_attempt_at`). |
| `succeeded` | sí | La orden quedó creada en SAP. |
| `failed` | sí | Error definitivo. Requiere corregir el dato y reintentar a mano. |
| `dead_letter` | sí | Error transitorio con los intentos agotados. Queda a la vista en el panel. |

La cola toma un `retrying` solo cuando llega su hora y reparte el trabajo en el orden en que cada job quedó disponible (`COALESCE(next_attempt_at, created_at)`): un job que reintenta seguido no deja sin atender a los nuevos. Lo respalda el índice parcial por expresión `ix_sync_jobs_claimable`. `failed` y `dead_letter` son terminales para el worker, pero el panel puede reabrirlos.

### Backoff exponencial con jitter

La espera tras el intento *n* es `min(MaxDelay, BaseDelay × 2^(n-1))` multiplicada por un factor al azar entre **0,5 y 1**. El jitter evita que los jobs que fallaron juntos por una misma caída vuelvan a golpear a SAP todos a la vez cuando se recupere. Con los valores por defecto (`Worker__Retry__*`):

| Parámetro | Valor |
|---|---|
| Intentos máximos | 12 |
| Espera base | 30 s |
| Tope de la espera | 1 hora |

| Tras el intento | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Espera (sin jitter) | 30 s | 1 min | 2 min | 4 min | 8 min | 16 min | 32 min | 1 h | 1 h | 1 h | 1 h |

**Ventana total de supervivencia.** Sumando las 11 esperas, un job atraviesa una caída de SAP de **entre 2 h 32 min y 5 h 4 min** de espera acumulada (el máximo sin jitter es 5 h 3 min 30 s; el mínimo es la mitad; el promedio con jitter es de unas **3 h 48 min**), más lo que dure cada intento (como mucho, el tiempo de espera de las llamadas a SAP, 30 s por defecto). Si la caída o la mantención dura más, el job pasa al dead letter y se rescata con un reintento manual al volver SAP. Para ventanas más largas sin cambiar el código: subir `Worker__Retry__MaxAttempts` (hasta 100) o el tope; cada intento extra con el tope por defecto suma entre 30 y 60 minutos.

Un job que **nunca llega a cerrarse** (el worker que lo toma cae o excede el lease una y otra vez) también acumula intentos: cada toma de un lease vencido cuenta. Al pasar del máximo, el siguiente worker que lo toma no lo procesa y lo manda al dead letter (`job.attempts_exhausted`). Así un job que tumba al worker no se reintenta sin fin.

### Reintento manual

`POST /admin/jobs/{id}/retry` y `POST /admin/jobs/retry` (hasta 100 ids) dejan el job `pending` de inmediato con un **presupuesto de intentos nuevo** (`cycle_attempts` vuelve a 0), conservando el historial de intentos y el último error. Sirve desde `failed`, `dead_letter` y `retrying` (para saltarse la espera). Un job en proceso o que ya terminó con éxito responde 409. Cada job se procesa por separado y el resultado se informa por id: que uno no se pueda reintentar no impide reintentar a los demás.

`attempt_count` es el historial de toda la vida del job; `cycle_attempts`, el presupuesto del ciclo actual. Un webhook que reenvía un pedido cuyo job ya está en dead letter se sigue ignorando como duplicado (la recepción es idempotente por origen, id externo y tipo): reabrir un job es una acción explícita del panel, no un efecto lateral de la tienda.

### Idempotencia durante los reintentos

Reintentar es seguro porque cada paso de la sincronización de un pedido es idempotente ([ADR 0008](0008-sincronizacion-de-pedidos.md)): la orden se busca por `NumAtCard = WC-{id}` antes de crearla, el cliente tiene un `CardCode` determinista y los mapeos se guardan a medida que se resuelven, de modo que un intento que falla a mitad de camino deja avanzado lo que logró. Las pruebas lo demuestran con SAP caído, errores 5xx en distintos puntos del flujo, doce pedidos concurrentes y un job al que se le crea la orden justo antes de caer: siempre una sola orden y un solo cliente.

### Autenticación del panel

Todo lo que cuelga de `/admin` exige la cabecera `X-Api-Key`. El webhook se protege con su firma HMAC; `/health` y la documentación en desarrollo quedan abiertos.

- **Middleware por prefijo**, no un filtro por endpoint: una ruta que no existe bajo `/admin` también responde 401, así que no se puede explorar el panel sin clave, y ningún endpoint futuro queda abierto por olvido.
- **Varias claves** separadas por coma en `Admin__ApiKey` (en Compose, `ERPSYNC_ADMIN_API_KEY`), cada una de 32 a 256 caracteres sin espacios, para **rotarlas sin cortar el acceso** (procedimiento en [variables de entorno](../variables-de-entorno.md#rotar-la-clave-sin-cortar-el-acceso)). Se valida al iniciar.
- **Comparación en tiempo constante**: se reduce cada clave a su SHA-256 (de largo fijo, así que tampoco filtra el largo) y se comparan los hashes con `FixedTimeEquals`, recorriendo **todas** las claves y combinando con `|` en vez de `||`.
- **Una cabecera repetida es ambigua** y se rechaza; la clave en la consulta o en `Authorization` no sirve.
- **Sin clave configurada el panel no queda abierto**: responde 503 con un mensaje que dice qué variable definir, y el log lo avisa al arrancar. El hub sigue recibiendo pedidos. La alternativa, que el hub no arrancara, habría convertido un panel sin configurar en una caída del webhook.
- **La clave nunca se registra**, ni la presentada ni las configuradas. Un acceso rechazado queda como advertencia con método, ruta e IP.

### Datos personales

El payload de un pedido trae nombre, correo, teléfono, dirección, RUT y la IP del comprador.

- El detalle de un job **omite el payload por defecto**; solo se entrega con `?includePayload=true`, y se lee de la base de datos aparte, para no traer datos personales del disco cuando nadie los pidió.
- **Ningún log los lleva.** Una prueba procesa pedidos con éxito, con RUT inválido, con cliente inhabilitado, con SKU desconocido, con SAP caído hasta el dead letter y con un payload incompleto, llama al panel pidiendo el payload, y comprueba que ninguno de los datos aparece en ningún evento de log (mensaje, propiedades o excepción), que son los mismos que Serilog envía a Seq. La prueba encontró fugas reales, ya corregidas: el mensaje de `order.invalid_tax_id` copiaba el RUT, el de `order.inactive_customer` copiaba el nombre y el código del cliente, y los logs de «cliente creado» y «orden creada» llevaban el `CardCode`, que para un cliente nuevo se deriva del RUT. Los mensajes ahora dicen qué pedido es y qué hacer, y el valor se consulta en el detalle del job con `includePayload=true`.
- Los mensajes de error que devuelve SAP se registran tal cual los entrega; el mock no repite datos del cliente, pero con un SAP real conviene revisar qué textos de error incluye.

### Sincronización completa de inventario programada

La sincronización incremental depende de `UpdateDate` ([ADR 0009](0009-sincronizacion-de-stock-y-precios.md)), un supuesto que hay que validar con un SAP real. Como respaldo, cada `Sync__Inventory__FullInterval` (24 horas por defecto, cero la desactiva) la ejecución periódica es completa: lee todo el maestro y envía todo, ignorando la foto. El intervalo se mide desde la **última completa exitosa guardada en la base** (también la manual), de modo que sobrevive a reinicios; las completas que fallaron o terminaron con errores no cuentan, así que se reintentan en el intervalo siguiente. La decisión se toma **dentro del runner, con el lock tomado**, para que dos instancias no hagan dos completas seguidas.

### Simulación de fallas en el mock

`PUT /_mock/faults` inyecta en el Service Layer simulado caída total (503 o conexión cortada), latencia, errores 5xx intermitentes (deterministas con `ErrorEveryNth` o aleatorios con `ErrorRate`) y expiración de sesión forzada. Los deterministas sirven para pruebas repetibles; el azar, para comprobar que no hay un patrón que las pruebas dejen sin cubrir. `/health` y la administración nunca se ven afectados.

### Métricas

`GET /admin/metrics`: jobs por estado (todos los estados, también en cero), la antigüedad del pendiente más viejo (una cifra que crece indica workers detenidos o sin dar abasto), cuándo toca el próximo reintento, y la última ejecución de inventario junto con la última exitosa. Solo JSON; Prometheus queda fuera de alcance.

### Apagado

Si el servicio se está apagando, cualquier falla del procesamiento de un job, no solo la cancelación (por ejemplo, un servicio ya desechado), se trata como consecuencia del apagado: el job se libera y no se programa un reintento.

## Alternativas descartadas

- **Reintentos en proceso con Polly** dentro del adaptador: no sobreviven a un reinicio, bloquean un worker durante la espera y no dejan nada visible en el panel. La cola persistente sí.
- **Una tabla de reintentos aparte**: los datos viven en el propio job (`next_attempt_at`, `cycle_attempts`) y el índice parcial basta.
- **Un único estado terminal**: se perdería en el panel la diferencia entre «esto necesita que alguien corrija un dato» y «SAP estuvo caído demasiado tiempo».
- **ASP.NET Core Authentication con esquemas/JWT** para el panel: sobredimensionado para una clave compartida con rotación; el middleware es más simple y su comportamiento, más fácil de probar.
- **Backoff sin jitter**: tras una caída larga, todos los jobs volverían a golpear a SAP en el mismo instante.

## Consecuencias

- Credenciales de SAP incorrectas se clasifican como `sap.session`, transitorio: el job se reintenta durante horas antes de llegar al dead letter. El health check (`Degraded`) y el log lo hacen evidente; el costo de clasificarlo como definitivo sería dejar jobs fallidos por una caída de login pasajera.
- Un `job.unexpected` por un defecto real se reintenta hasta el máximo antes de quedar visible; es el precio de no distinguir un defecto de un fallo pasajero. El nivel `Error` y la excepción completa en cada intento evitan que pase desapercibido.
- Cambiar la clave del panel exige reiniciar el hub; la rotación con varias claves lo hace sin cortar el acceso.
- El panel no tiene roles: quien tiene una clave puede leer y reintentar. Para más de un nivel de acceso haría falta un esquema de autenticación completo.
- Las métricas cuentan filas de `sync_jobs` por estado: es barato con miles de jobs y deja de serlo con millones; llegado el caso se archivan los jobs terminados.
