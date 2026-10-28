# 0012. Roles del panel, autenticación con ASP.NET Core y auditoría de acciones

- **Estado:** Aceptada
- **Fecha:** 2026-10-28

## Contexto

El [ADR 0010](0010-resiliencia-reintentos-dead-letter-y-panel.md) protegía el panel con una clave única (o varias equivalentes) y lo dejó anotado como límite: quien tiene una clave puede leer y también reintentar, sincronizar y, con las fases siguientes, cerrar el circuito o eliminar mapeos. Un monitoreo que solo necesita mirar no debería poder actuar, y cuando alguien actúa tiene que quedar constancia de *qué clave* lo hizo.

## Decisión

### Dos niveles de clave

| Nivel | Variable (Compose) | Puede |
|---|---|---|
| Lectura | `Admin__ReadApiKeys` (`ERPSYNC_ADMIN_READ_API_KEYS`) | Listar y ver jobs, métricas, mapeos, circuitos y auditoría. |
| Operación | `Admin__OperateApiKeys` (`ERPSYNC_ADMIN_OPERATE_API_KEYS`) | Todo lo anterior, y además reintentar jobs, disparar sincronizaciones, cerrar el circuito y eliminar mapeos. |

Cada variable admite **varias claves separadas por coma**, con el mismo procedimiento de rotación de antes (se agrega la nueva, se pasan los clientes, se retira la vieja). Una clave de operación también lee. Una misma clave en las dos listas **impide arrancar**: dejaría ambiguo qué puede hacer. Las reglas de cada clave no cambian: de 32 a 256 caracteres, sin espacios ni caracteres de control, sin repetidas.

> **Cambio incompatible.** `ERPSYNC_ADMIN_API_KEY` (`Admin__ApiKey`) **desaparece, sin alias**. La versión v0.5.0 no estaba desplegada en ninguna parte, así que no hay clientes que migrar; conservar un alias habría dejado una variable que no dice qué nivel da. Quien la tenga definida debe pasarla a `ERPSYNC_ADMIN_OPERATE_API_KEYS` (equivale a lo que daba antes). Sin ninguna de las dos variables el panel sigue respondiendo 503.

### `AuthenticationHandler` y políticas

La autenticación pasa de un middleware propio a las piezas de ASP.NET Core:

- **`AdminApiKeyAuthenticationHandler`** (esquema `AdminApiKey`) lee `X-Api-Key`, compara contra todas las claves en tiempo constante (SHA-256 y `FixedTimeEquals`, sin cortocircuito) y emite una identidad con el rol (`read` u `operate`; operar incluye leer) y la huella de la clave. Una cabecera repetida se rechaza.
- **Políticas** `AdminRead` y `AdminOperate`. El grupo `/admin` entero exige `AdminRead`; los endpoints de acción agregan `AdminOperate`. Una ruta que no existe bajo `/admin` también exige clave (hay una ruta comodín dentro del grupo), así que no se puede explorar el panel sin una.
- El desafío conserva los códigos: **401** sin clave o con una inválida (con `WWW-Authenticate`), **503** sin ninguna clave configurada, y nuevo **403** cuando una clave de lectura intenta una acción. La acción no se ejecuta.

### Auditoría

Toda acción manual deja una entrada: reintentar jobs (una por job), disparar la sincronización de inventario, cerrar el circuito y eliminar mapeos.

- Cada entrada guarda cuándo, la acción (`job.retry`, `inventory.sync`, `circuit.close`, `mapping.delete_product`, `mapping.delete_customer`), sobre qué, cómo terminó (`retried`, `not_found`, `deleted`…), el nivel de la clave y su **huella**. Además se registra en el log (Seq) con las mismas propiedades.
- **La huella son los 8 primeros caracteres hexadecimales del SHA-256 de la clave.** Distingue qué clave se usó, pero con claves aleatorias de 32 caracteres o más no permite recuperarla ni acercarse a ella (32 bits de un hash de 256). **La clave nunca se guarda ni se registra**, ni siquiera parcial.
- Las acciones rechazadas (401, 403) no se auditan como hechas: no ocurrieron. Quedan como advertencias en el log con la huella (en el caso del 403).
- Lo que identifica a una persona no entra en la auditoría: para un invitado, la clave del mapeo es su correo, y se registra con el correo oculto (`email:[correo]`).
- Si escribir la auditoría falla, la acción ya ocurrió: no se le oculta a quien la pidió, pero queda un error en el log con la acción afectada.

### La auditoría es de solo inserción

`admin_audit` no se modifica ni se borra, y esto se garantiza en tres capas:

1. **El puerto** (`IAdminAudit`) solo ofrece `RecordAsync` y `ListAsync`: no existe ningún método para actualizar o eliminar.
2. **La API**: el panel solo expone `GET /admin/audit`. No hay endpoints para modificar ni borrar entradas, y una prueba comprueba que `PUT`, `PATCH`, `DELETE` y `POST` sobre esa ruta y sobre una entrada concreta no tienen éxito.
3. **La base de datos**: un disparador `BEFORE UPDATE OR DELETE` y otro `BEFORE TRUNCATE` rechazan el cambio con un error (`admin_audit es de solo inserción`). Ni siquiera una sesión con acceso a la base de datos modifica o borra lo registrado sin quitar antes el disparador, y quitarlo es en sí un cambio de esquema visible.

Esto protege contra errores y contra quien tenga una clave de operación; no contra quien administre PostgreSQL, que puede quitar el disparador. Para ese caso hace falta enviar la auditoría fuera de la base (Seq ya la recibe) o a un almacenamiento de solo anexar.

## Alternativas descartadas

- **JWT o un proveedor de identidad**: sobredimensionado para un panel de operación con un puñado de claves; no hay usuarios humanos con sesión. Si el hub llegara a tener varios operadores, la huella de la clave ya prepara el terreno (cada operador, su clave).
- **Un rol por endpoint, configurable**: más flexible, más difícil de razonar y de auditar. Dos niveles cubren el caso (mirar o actuar).
- **Conservar `ERPSYNC_ADMIN_API_KEY` como alias de operación**: ver arriba; innecesario sin despliegues previos.
- **Auditar solo en el log**: Seq puede estar apagado o retener poco; la tabla da un registro propio, consultable desde el panel y blindado contra cambios.
- **Hash largo o HMAC con una sal secreta como huella**: más resistente en teoría, pero obliga a guardar otro secreto. Con claves de entropía alta, 32 bits de un SHA-256 no permiten ataque alguno y bastan para distinguir un puñado de claves.

## Consecuencias

- Las huellas de dos claves distintas podrían coincidir (32 bits): es posible, no es un identificador único, y la entrada incluye además el nivel. Con la cantidad de claves de este panel, la probabilidad es despreciable.
- Cambiar o retirar una clave no borra su historia: la huella queda en la auditoría. Conviene anotar fuera del sistema a quién se entregó cada clave.
- Los endpoints futuros del panel heredan la autenticación del grupo `/admin`; los que actúen deben agregar `AdminOperate` y auditar, y una prueba por endpoint debe comprobar el 403.
