# 0005. Sesión del Service Layer: login perezoso y renovación reactiva

- **Estado:** Aceptada
- **Fecha:** 2026-10-22

## Contexto

El Service Layer exige iniciar sesión (`POST /b1s/v1/Login`) y enviar la cookie `B1SESSION` en cada petición. La sesión expira tras un período de inactividad (30 minutos por defecto) y también puede cerrarse por causas ajenas al cliente, como un reinicio del servicio. Iniciar sesión es costoso y las sesiones son un recurso limitado del servidor, de modo que no conviene hacer un login por petición.

El hub es un servicio de larga vida con un worker que hace llamadas concurrentes. Cuando una sesión expira, muchas peticiones pueden descubrirlo a la vez; si cada una iniciara sesión por su cuenta, se abrirían decenas de sesiones y las demás quedarían invalidadas o desperdiciadas.

## Decisión

`SapB1.Client` mantiene **una sola sesión compartida por proceso** y la gestiona un `DelegatingHandler` del `HttpClient`, transparente para quien usa el cliente:

- **Login perezoso:** la sesión se abre en la primera petición, no al arrancar.
- **Renovación reactiva:** si SAP responde `401`, el handler descarta esa sesión, inicia otra y **reintenta la petición una sola vez**. Si el segundo intento también falla, lanza `SapB1SessionException`.
- **Un solo login ante concurrencia:** el login se serializa con un semáforo y, dentro de la sección crítica, se revisa de nuevo si otra petición ya renovó la sesión. La invalidación compara la sesión por referencia, de modo que una petición lenta que falló con la sesión vieja no destruye la nueva.
- **Estado en un singleton:** `IHttpClientFactory` recicla los handlers cada pocos minutos, por lo que la sesión vive en un servicio singleton y no en el handler.
- **Reintento seguro de cualquier método:** el Service Layer valida la sesión antes de procesar la petición, así que un `401` nunca deja una operación a medias y reintentar un `POST` no duplica nada. Para poder reenviarlas, el cuerpo de la petición se copia en memoria antes de enviarla.
- **Cookies:** se conservan `B1SESSION` y `ROUTEID` (este último lo usa el balanceador de carga del Service Layer). Si la respuesta de login no trae `Set-Cookie`, se construye `B1SESSION` con el `SessionId` del cuerpo.
- **Cierre opcional:** `LogoutAsync` cierra la sesión explícitamente; no se invoca solo.

No se renueva de forma proactiva (por reloj antes de que expire): el servidor también puede invalidar sesiones por su cuenta, así que la renovación reactiva es imprescindible, y añadir otra vía sólo sumaría complejidad.

## Consecuencias

- Las llamadas del hub no necesitan saber nada de sesiones, y una expiración cuesta un `401` más un login, una única vez por proceso.
- Cada instancia del hub tiene su propia sesión; el número de sesiones abiertas crece con el número de instancias.
- Una instancia del cliente corresponde a una sola combinación de servidor, sociedad y usuario. Para varias sociedades haría falta un registro por clave.
- Un `401` que no venga del Service Layer, por ejemplo de un proxy, provoca un login y un reintento adicionales antes de fallar.
- La versión `v1` del Service Layer está basada en OData v3, que SAP marca como obsoleto desde el Feature Pack 2405 a favor de `v2` (OData v4, con anotaciones `@odata.*`). El proyecto sigue `v1` por ahora; migrar a `v2` cambiará el envoltorio de las respuestas y la paginación, y requerirá un ADR propio.
