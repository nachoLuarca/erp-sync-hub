# 0004. SAP simulado según el contrato del Service Layer

- **Estado:** Aceptada
- **Fecha:** 2026-10-22

## Contexto

No se dispone de una instancia de SAP Business One para desarrollar ni para ejecutar la integración en CI. Un *stub* ad hoc que devuelve respuestas inventadas daría una falsa sensación de que la integración funciona y ocultaría los problemas reales: sesiones que expiran, formato de errores, paginación.

## Decisión

Se construye `SapB1.Mock`, una Minimal API que replica el **contrato oficial** del Service Layer (`/b1s/v1/...`): `Login`, `Items`, `BusinessPartners` y `Orders`, incluidos su formato de errores y la expiración de sesión. No se inventan endpoints: se sigue la documentación oficial de SAP Business One Service Layer.

- El mock tiene su propia persistencia y no depende de ningún proyecto `ErpSyncHub.*`.
- El hub solo conoce `SAP__BaseUrl`; apuntarlo a un SAP real no requiere cambios de código.
- El acceso a SAP pasa por `SapB1.Client`, un cliente tipado independiente del hub y publicable en NuGet. Gestiona el inicio de sesión y su renovación.
- En la fase de resiliencia, el mock permitirá **simular fallas** (latencia, errores 5xx, sesión expirada) para probar reintentos y *dead letter*.

## Consecuencias

- Se puede desarrollar y probar la integración completa de forma local y en CI, sin SAP.
- Los errores propios del Service Layer (por ejemplo, sesión expirada) se ejercitan desde el principio, no al integrar con un SAP real.
- El mock cubre solo el subconjunto del contrato que el hub usa; ampliar el alcance exige mantenerlo alineado con la documentación oficial.
- El mock puede diferir de una instalación real en comportamientos no documentados, por lo que la validación contra un SAP real sigue siendo necesaria antes de un uso productivo.
