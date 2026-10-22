# 0002. Clean Architecture con regla de dependencias verificada

- **Estado:** Aceptada
- **Fecha:** 2026-10-22

## Contexto

El hub integra sistemas externos (WooCommerce, SAP, PostgreSQL) cuyos detalles cambian con más frecuencia que las reglas de negocio. Queremos poder probar la lógica de sincronización sin red ni base de datos, y poder sustituir un sistema externo (por ejemplo, el SAP simulado por uno real) sin tocar la lógica.

## Decisión

Se organiza la solución en capas concéntricas con dependencias que apuntan siempre hacia adentro:

- `Domain`: entidades y reglas puras, sin dependencias externas.
- `Application`: casos de uso e interfaces de los puertos (`ISapGateway`, `IStoreGateway`…). Depende solo de `Domain`.
- `Infrastructure`: implementa los puertos con EF Core, clientes HTTP y adaptadores. Depende de `Application`, `Domain` y `SapB1.Client`.
- `Api`: Minimal APIs y hospedaje del worker. Referencia `Infrastructure` únicamente para registrar dependencias.
- `SapB1.Client` y `SapB1.Mock` no dependen de ningún proyecto `ErpSyncHub.*`, de modo que el cliente pueda publicarse como paquete NuGet independiente.

La regla se **verifica automáticamente**: tests con NetArchTest sobre los tipos y una comprobación de los `ProjectReference` de cada proyecto con lista exacta de referencias permitidas.

Los errores de negocio se modelan con el patrón *Result*; las excepciones se reservan para lo excepcional.

## Consecuencias

- La lógica de negocio se prueba con tests unitarios rápidos, sin infraestructura.
- Cambiar un sistema externo afecta solo a `Infrastructure`.
- Hay más proyectos y algo de código de traducción entre capas (mapeos), un costo asumido a cambio del aislamiento.
- Una violación de la regla rompe el build de CI, en lugar de descubrirse en una revisión.
