# 0014. Despliegue de demostración gratuito en Render y Neon

- **Estado:** Aceptada
- **Fecha:** 2026-10-29

## Contexto

El proyecto es una vitrina: quien lo evalúa debería poder ver el hub funcionando sin clonar el repositorio ni levantar Docker. Eso pide un despliegue público, gratuito y reproducible desde el propio repositorio. No hay un SAP real: el hub se despliega junto con `SapB1.Mock` ([ADR 0004](0004-sap-simulado-segun-contrato-service-layer.md)), que lo reemplaza sin cambios de código porque el hub solo conoce `SAP__BaseUrl`.

Restricciones: costo cero, sin secretos en el repositorio, y las garantías del hub (cola en PostgreSQL con `FOR UPDATE SKIP LOCKED`, locks de sesión, circuit breaker compartido) no pueden degradarse para acomodarse al hosting.

## Decisión

- **Render, plan gratuito, dos servicios web Docker** (`erp-sync-hub` y `sap-mock`), declarados en un Blueprint `render.yaml`. Cada uno se construye con el `Dockerfile` que ya usa Compose; no hay imágenes ni pasos de build especiales para Render. La región es Virginia.
- **PostgreSQL en Neon** (AWS us-east-1, misma zona que Render). Se conecta por el endpoint **directo**, no por el pooler: el hub usa `pg_advisory_lock` de sesión, que un pooler en modo transacción no garantiza.
- **Cero secretos en el repositorio.** Toda variable sensible se declara con `sync: false` y se carga a mano en Render. Un test de arquitectura (`DeploymentFilesTests`) falla si una variable de nombre sensible lleva valor en `render.yaml`.
- **Migraciones al arrancar, controladas por variable.** El Blueprint fija `Database__MigrateOnStartup=true` porque el plan gratuito corre una sola instancia. Con varias instancias se desactiva y se migra como paso aparte.
- **El mock es público, así que se protege.** `/_mock` (reset e inyección de fallas) exige `X-Api-Key` cuando `SapMock__AdminApiKey` está definida. Es opcional para no cambiar el uso local ni las pruebas, y obligatoria por Blueprint en el despliegue.
- **Sin Seq ni tienda.** En producción los logs van a la consola en JSON compacto. El pedido de la demo lo envía `scripts/demo-render.ps1`, que firma el webhook como lo haría WooCommerce.
- **Despliegue automático solo con CI en verde** (`autoDeployTrigger: checksPass`).
- **Sin *keep-alive*.** No se hace ping periódico para evitar que los servicios se duerman.

## Alternativas descartadas

- **PostgreSQL de Render:** la base gratuita de Render caduca; Neon mantiene la base y suspende el cómputo sin actividad.
- **Neon con pooler:** rompe los locks de sesión que usa la exclusión mutua de la sincronización de inventario ([ADR 0009](0009-sincronizacion-de-stock-y-precios.md)).
- **Mock con persistencia:** el plan gratuito no tiene disco; añadirle una base solo para la demo complicaría un simulador cuyo valor es ser simple. El hub ya tolera que SAP pierda clientes ([ADR 0008](0008-sincronizacion-de-pedidos.md)).
- **Mantener los servicios despiertos con un ping externo:** oculta la limitación del plan en vez de documentarla, y gasta las horas gratuitas de instancia.
- **Otros hostings (Fly.io, Railway):** sus planes gratuitos son más restrictivos o requieren tarjeta; Render con Blueprint deja todo el despliegue en un archivo versionado.

## Consecuencias

- Los servicios **se duermen** tras un rato sin tráfico y la primera petición tarda cerca de un minuto. El worker y la sincronización periódica solo corren mientras el hub está despierto; los jobs pendientes esperan en PostgreSQL, sin pérdida. Un webhook real que llegue con el hub dormido puede agotar el tiempo de la tienda: aceptable para una demo, inaceptable en producción, donde se necesita un plan que no duerma.
- El mock **pierde su estado** al dormirse o reiniciarse.
- Sin red privada en el plan gratuito, el hub llama al mock por HTTPS público.
- Sin tienda desplegada, la sincronización de stock y precios queda desactivada en la demo; solo se muestra el flujo de pedidos.
- Pasar a un despliegue real cambia `SAP__BaseUrl` y los planes; no cambia el código ni el `Dockerfile`.
