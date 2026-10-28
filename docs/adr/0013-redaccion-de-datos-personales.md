# 0013. Redacción de datos personales en mensajes externos y logs

- **Estado:** Aceptada
- **Fecha:** 2026-10-28

## Contexto

El payload de un pedido trae nombre, dirección, teléfono, correo y RUT del cliente. El [ADR 0010](0010-resiliencia-reintentos-dead-letter-y-panel.md) los mantuvo fuera de los logs y de los mensajes propios, y añadió un redactor de patrones que oculta RUT y correos en lo que SAP o la tienda repiten en sus errores (el caso real fue `Business partner 'C18765432-7' does not exist`). Quedaron dos huecos conocidos: un **nombre o una dirección** escritos por un sistema externo no tienen forma que un patrón reconozca, y nada impedía que alguien, por descuido, **agregara un campo personal a un log** más adelante. Además, un sistema externo puede responder con una página entera de HTML, y el mensaje completo terminaba en el job y en los logs.

## Decisión

### Redacción contextual

Mientras se procesa un pedido, el hub **conoce** sus datos personales: son los del pedido. `PersonalDataScope` los guarda en un ámbito que fluye con la ejecución asíncrona (`AsyncLocal`; cada job, el suyo, aunque corran varios a la vez) y `OrderSyncService` lo abre apenas lee el pedido. Los mensajes de SAP y de la tienda pasan por `ExternalMessageSanitizer`, que oculta:

| Dato | Se busca | Se reemplaza por |
|---|---|---|
| Nombre completo y empresa | el valor exacto | `[nombre]` |
| Cada línea de la dirección | el valor exacto | `[dirección]` |
| Correo | el valor exacto, y además el patrón de correo | `[correo]` |
| Teléfono | por sus **dígitos** (con o sin `+56`, espacios, guiones, paréntesis) | `[teléfono]` |
| RUT | el valor del pedido en sus formas (con puntos, con guion, compacto) y además el patrón de RUT | `[RUT]` |

- **Se normaliza antes de comparar**: mayúsculas, tildes y espacios repetidos no importan (`JOSÉ  PÉREZ` encuentra `jose perez`), y el texto original conserva sus tildes en el resto del mensaje.
- **La tabla de tildes es propia.** El hub corre con `InvariantGlobalization` y, en ese modo, `string.Normalize` no descompone las letras con tilde (lo descubrió una prueba: `ñ` quedaba como `ñ`). Se pliegan a mano las letras latinas del Latin-1 y Latin Extended-A.
- **Un valor de menos de 4 caracteres no se busca** (ni «Ana» ni «S/N»): ocultarlo destruiría los mensajes sin proteger nada. Tampoco se oculta un valor que sea parte de otra palabra (`Pedro Soto` no toca `Pedro Sotomayor`).
- Se aplica **además** de los patrones, no en su lugar: el RUT que SAP repite sin que figure en este pedido sigue oculto.

### Largo máximo

`ExternalMessageSanitizer` recorta el mensaje a `Messages__ExternalMaxLength` (`ERPSYNC_EXTERNAL_MESSAGE_MAX_LENGTH`, 300 por defecto, entre 50 y 2000). **Primero oculta y después recorta**, para que el corte no deje a medias un dato que ya no se podría reconocer (un RUT cortado en `18.765`). Un par sustituto nunca se parte por la mitad.

### Política de Serilog

`PersonalDataLogging` agrega a la configuración de Serilog:

- **Un destructurador** que impide desarmar los objetos del pedido (`WooCommerceOrder`, direcciones, clientes de SAP, el job y su detalle) con `{@Objeto}`: se registra `[personal]`.
- **Un enriquecedor** que, antes de que el evento llegue a la consola o a Seq: oculta cualquier propiedad con nombre de dato personal (`Email`, `Phone`, `Rut`, `TaxId`, `FirstName`, `LastName`, `CardName`, `Address1`, `Billing`, `Payload`…, sin distinguir mayúsculas), oculta los objetos del pedido escritos con `ToString` (el de un `record` imprime todos sus campos) y limpia, en cualquier texto, los patrones de RUT y correo y los datos del pedido en proceso. Recorre estructuras, listas y diccionarios.

Como el mensaje se arma con las propiedades, ocultarlas lo cubre.

## Alternativas descartadas

- **No registrar jamás mensajes externos**: se perdería el motivo de un rechazo (`does not exist`, `Item 'ZZZ'`), que es lo que sirve para corregirlo.
- **Una lista global de «palabras prohibidas»**: no existe una lista de nombres; el contexto del pedido sí.
- **Redactar con una expresión regular por cada dato**: frágil con tildes y espacios, y vulnerable a metacaracteres; la búsqueda exacta sobre el texto normalizado no tiene esos problemas.
- **Confiar en la disciplina** de no loguear campos personales: ya falló una vez (el RUT en un mensaje de SAP). Una política que lo impide es más barata que una revisión.

## Consecuencias

- **Es una defensa por coincidencia exacta, no una garantía.** Un dato escrito distinto por el sistema externo (otro orden de las palabras, una abreviatura, un nombre partido en dos mensajes) no se reconoce. Los mensajes propios del hub nunca incluyen datos personales, y esto cubre lo que no controla.
- **El texto de una excepción no se limpia.** Serilog no permite reemplazar la excepción de un evento desde un enriquecedor; una excepción cuyo mensaje contenga datos personales los dejaría pasar. Por eso los mensajes que el hub deriva de excepciones (`job.unexpected`, errores de red) pasan por el sanitizador antes de guardarse, y las excepciones completas solo se registran en errores inesperados, nivel Error.
- Comprobar la política en Seq contra datos reales sigue siendo parte de la prueba de punta a punta (`scripts/e2e-tienda.ps1`, paso 11).
- La lista de nombres de propiedad y de tipos personales hay que mantenerla al agregar tipos que lleven datos del cliente; una prueba por tipo evita olvidarlos.
