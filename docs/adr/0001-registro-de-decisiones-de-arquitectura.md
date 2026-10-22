# 0001. Registrar las decisiones de arquitectura

- **Estado:** Aceptada
- **Fecha:** 2026-10-22

## Contexto

El proyecto toma decisiones que condicionan su evolución: capas, persistencia, estrategia de pruebas, cómo simular SAP. Sin un registro, el porqué de cada una se pierde en el historial de commits o en la memoria de quien las tomó, y se vuelven a discutir sin información nueva.

## Decisión

Cada decisión de arquitectura relevante se documenta como un *Architecture Decision Record* (ADR), siguiendo el formato de Michael Nygard: contexto, decisión y consecuencias.

- Los ADR viven en `docs/adr/`, se numeran de forma correlativa y se escriben en español.
- Un ADR aceptado no se edita en su contenido. Si la decisión cambia, se crea otro ADR que lo reemplaza y el anterior pasa a *Reemplazada*.
- Un cambio que introduce una decisión de este tipo incluye su ADR en la misma rama.

## Consecuencias

- Queda trazabilidad del porqué de cada decisión, útil para quien se incorpore al proyecto.
- Escribir el ADR obliga a explicitar alternativas y costos antes de implementar.
- Hay un costo de mantenimiento pequeño: el índice de `docs/adr/README.md` debe actualizarse con cada ADR nuevo.
