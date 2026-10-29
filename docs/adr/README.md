# Registro de decisiones de arquitectura

Cada decisión relevante se documenta en un ADR numerado e inmutable: si una decisión cambia, se escribe un ADR nuevo que reemplaza al anterior y el antiguo pasa a *Reemplazado*.

| N.º | Decisión | Estado |
|---|---|---|
| [0001](0001-registro-de-decisiones-de-arquitectura.md) | Registrar las decisiones de arquitectura | Aceptada |
| [0002](0002-clean-architecture.md) | Clean Architecture con regla de dependencias verificada | Aceptada |
| [0003](0003-postgresql-como-cola.md) | PostgreSQL como cola de jobs | Aceptada |
| [0004](0004-sap-simulado-segun-contrato-service-layer.md) | SAP simulado según el contrato del Service Layer | Aceptada |
| [0005](0005-sesion-del-service-layer.md) | Sesión del Service Layer: login perezoso y renovación reactiva | Aceptada |
| [0006](0006-excepciones-tipadas-en-sapb1-client.md) | Excepciones tipadas en SapB1.Client | Aceptada |
| [0007](0007-inbox-y-cola-con-lease.md) | Inbox y cola con lease para los pedidos de la tienda | Aceptada |
| [0008](0008-sincronizacion-de-pedidos.md) | Sincronización de pedidos: idempotencia, clientes, productos y precios | Aceptada |
| [0009](0009-sincronizacion-de-stock-y-precios.md) | Sincronización de stock y precios: lectura incremental, foto sincronizada y exclusión mutua | Aceptada |
| [0010](0010-resiliencia-reintentos-dead-letter-y-panel.md) | Resiliencia: reintentos con backoff, dead letter, panel autenticado y datos personales | Aceptada |
| [0011](0011-circuit-breaker-de-sap.md) | Circuit breaker de SAP compartido por todas las instancias | Aceptada |
| [0012](0012-roles-del-panel-y-auditoria.md) | Roles del panel, autenticación con ASP.NET Core y auditoría de acciones | Aceptada |
| [0013](0013-redaccion-de-datos-personales.md) | Redacción de datos personales en mensajes externos y logs | Aceptada |
| [0014](0014-despliegue-gratuito-en-render-y-neon.md) | Despliegue de demostración gratuito en Render y Neon | Aceptada |
| [0015](0015-releases-por-tag-sin-pull-requests.md) | Releases disparados por tag, sin pull requests | Aceptada |

## Plantilla

```markdown
# NNNN. Título

- **Estado:** Propuesta | Aceptada | Reemplazada por [NNNN](...)
- **Fecha:** AAAA-MM-DD

## Contexto
## Decisión
## Consecuencias
```
