# Registro de decisiones de arquitectura

Cada decisión relevante se documenta en un ADR numerado e inmutable: si una decisión cambia, se escribe un ADR nuevo que reemplaza al anterior y el antiguo pasa a *Reemplazado*.

| N.º | Decisión | Estado |
|---|---|---|
| [0001](0001-registro-de-decisiones-de-arquitectura.md) | Registrar las decisiones de arquitectura | Aceptada |
| [0002](0002-clean-architecture.md) | Clean Architecture con regla de dependencias verificada | Aceptada |
| [0003](0003-postgresql-como-cola.md) | PostgreSQL como cola de jobs | Aceptada |
| [0004](0004-sap-simulado-segun-contrato-service-layer.md) | SAP simulado según el contrato del Service Layer | Aceptada |

## Plantilla

```markdown
# NNNN. Título

- **Estado:** Propuesta | Aceptada | Reemplazada por [NNNN](...)
- **Fecha:** AAAA-MM-DD

## Contexto
## Decisión
## Consecuencias
```
