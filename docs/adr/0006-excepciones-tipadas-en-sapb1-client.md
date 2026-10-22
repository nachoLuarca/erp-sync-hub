# 0006. Excepciones tipadas en SapB1.Client

- **Estado:** Aceptada
- **Fecha:** 2026-10-22

## Contexto

El proyecto usa el patrón *Result* para los errores de negocio (ADR 0002). `SapB1.Client`, en cambio, es un paquete NuGet independiente del hub: no puede depender de un tipo `Result` del hub ni imponer a otros consumidores una convención ajena. Hay que decidir cómo informa el cliente de los rechazos de SAP.

## Decisión

El cliente informa los errores con **excepciones tipadas**:

- `SapB1Exception` (base abstracta), con `StatusCode`, `SapErrorCode` y `SapErrorMessage`, tal como los entrega el Service Layer.
- `SapB1ApiException`: SAP rechazó la operación (validación, duplicado, solicitud inválida, error del servidor). Expone `IsNotFound` e `IsAlreadyExists` para los casos que quien llama suele querer distinguir.
- `SapB1SessionException`: no se pudo iniciar sesión o SAP siguió rechazándola tras renovarla (ADR 0005).
- `SapB1ErrorCodes` publica los códigos de error que el cliente interpreta.

Dos precisiones:

- Las consultas por clave (`GetAsync`) devuelven `null` cuando el registro no existe, porque es el resultado normal de una búsqueda y no un fallo; el resto de los rechazos sí lanzan.
- Los fallos de transporte (red, tiempo de espera, cancelación) **no se envuelven**: se propagan como `HttpRequestException` u `OperationCanceledException`, para que el consumidor pueda aplicar su propia política de resiliencia.

La conversión a `Result` es responsabilidad del **adaptador de `Infrastructure` del hub** (`ISapGateway`), que captura estas excepciones y las traduce a errores de negocio. De ese modo la regla del Result pattern se cumple en el borde del hub, y el cliente sigue siendo reutilizable.

## Alternativas descartadas

- **`Result<T>` propio dentro del cliente:** obliga a cada consumidor a adoptar esa convención y duplica un tipo que el hub ya define.
- **Lanzar también cuando un registro no existe en `GetAsync`:** vuelve ruidoso el caso más común, "¿existe ya este cliente?".

## Consecuencias

- El cliente se usa de forma idiomática en .NET y se publica sin acoplarse al hub.
- El hub debe capturar `SapB1Exception` de forma explícita en su adaptador; una excepción sin capturar es un defecto, no un error de negocio.
- Distinguir un rechazo de negocio de un fallo transitorio queda a cargo del adaptador: `SapB1ApiException` con estado `4xx` es un rechazo definitivo, mientras que `5xx`, `SapB1SessionException` y los errores de red son candidatos a reintento (Fase 4).
