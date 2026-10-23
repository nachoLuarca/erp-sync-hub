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

## Plantilla

```markdown
# NNNN. Título

- **Estado:** Propuesta | Aceptada | Reemplazada por [NNNN](...)
- **Fecha:** AAAA-MM-DD

## Contexto
## Decisión
## Consecuencias
```
