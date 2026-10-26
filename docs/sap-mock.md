# SapB1.Mock

SAP Business One simulado que replica el contrato del **Service Layer v1** (`/b1s/v1/...`), incluidos sus errores y la expiración de sesión. El hub solo conoce `SAP__BaseUrl`, así que apuntarlo a un SAP real no requiere cambios de código ([ADR 0004](adr/0004-sap-simulado-segun-contrato-service-layer.md)).

## Ejecutarlo

```bash
dotnet run --project src/SapB1.Mock        # http://localhost:5211
docker compose up -d --build sap-mock      # http://localhost:5201
```

El estado vive en memoria: reiniciar el proceso o llamar a `POST /_mock/reset` restaura los datos semilla.

## Configuración

Sección `SapMock` de `appsettings.json`, sobreescribible con variables de entorno (`SapMock__SessionTimeoutMinutes=1`). Se valida al iniciar.

| Clave | Valor por defecto | Descripción |
|---|---|---|
| `CompanyDb` | `SBODemoCL` | Sociedad aceptada en el login. |
| `UserName` / `Password` | `manager` / `manager` | Credenciales ficticias. |
| `SessionTimeoutMinutes` | `30` | Inactividad tras la cual expira la sesión (1 a 1440). |
| `DefaultPageSize` | `20` | Filas por página de las colecciones (1 a 1000). |
| `Version` | `1000160` | Versión informada en el login. |

## Endpoints

Todo `/b1s/v1` salvo `Login` exige la cookie `B1SESSION`; sin una sesión vigente responde `401` con el error de SAP.

| Método y ruta | Respuesta |
|---|---|
| `POST /b1s/v1/Login` | `200` con `SessionId`, `Version`, `SessionTimeout` y las cookies `B1SESSION` y `ROUTEID`. |
| `POST /b1s/v1/Logout` | `204`. |
| `GET /b1s/v1/Items` · `Items('A00001')` | Maestro de artículos (solo lectura). Incluye `UpdateDate` y `UpdateTime` (hora local de SAP), que se actualizan al comprometer stock: permiten la lectura incremental. |
| `GET /b1s/v1/BusinessPartners` · `BusinessPartners('C00001')` | Clientes y proveedores. |
| `POST /b1s/v1/BusinessPartners` | `201` con el socio creado. Sin `CardCode`, asigna el siguiente de la serie. |
| `PATCH /b1s/v1/BusinessPartners('C00001')` | `204`. Mezcla lo enviado sobre el estado actual y revalida. |
| `GET /b1s/v1/Orders` · `Orders(1)` | Órdenes de venta. |
| `POST /b1s/v1/Orders` | `201` con la orden creada. |
| `GET /health` | Salud del simulador. |

Las colecciones aceptan `$select`, `$filter`, `$orderby`, `$top`, `$skip` e `$inlinecount=allpages`, y se paginan con `odata.nextLink`. Con `Prefer: odata.maxpagesize=N` cambian el tamaño de página (0 desactiva la paginación) y la respuesta informa `Preference-Applied`.

`$filter` soporta `eq ne gt ge lt le`, `and`, `or`, `not`, paréntesis y las funciones `startswith`, `endswith`, `contains` y `substringof`. Las comparaciones de texto distinguen mayúsculas y minúsculas, y los nombres de propiedad también.

### Endpoints del simulador

Fuera del contrato de SAP y sin sesión; sirven para pruebas automáticas y para simular fallas.

| Método y ruta | Efecto |
|---|---|
| `POST /_mock/reset` | Restaura artículos, socios y órdenes a los datos semilla, y reinicia la numeración. |
| `POST /_mock/sessions/expire` | Invalida todas las sesiones abiertas, como si hubieran expirado. |
| `PATCH /_mock/items/{itemCode}` | Cambia stock físico (`InStock`), comprometido (`Committed`), precio (`Price`) o congela el artículo (`Frozen`); `WarehouseCode` y `PriceList` eligen el almacén y la lista (por defecto `01` y `1`). Actualiza `UpdateDate`/`UpdateTime`. Responde 204, 404 si no existe o 400 con valores negativos. |

### Simulación de fallas

`PUT /_mock/faults` activa fallas que se inyectan en las peticiones del Service Layer (`/b1s/v1`, incluido el login); `/health` y `/_mock` nunca se ven afectados. Reemplaza la configuración anterior y reinicia los contadores. `GET /_mock/faults` devuelve la configuración y cuántas veces se inyectó cada falla; `DELETE /_mock/faults` y `POST /_mock/reset` las quitan. Una configuración inválida responde 400 y no cambia nada.

| Campo | Efecto |
|---|---|
| `Outage` | Caída total: ninguna petición se atiende. |
| `OutageMode` | `Unavailable` (por defecto) responde 503 con un error de SAP; `ConnectionReset` corta la conexión sin responder, como un proceso caído. |
| `LatencyMs` | Demora cada petición esa cantidad de milisegundos (hasta 120 000). |
| `ErrorEveryNth` | Cada N-ésima petición responde con `ErrorStatusCode`. Es determinista: sirve para pruebas. |
| `ErrorRate` | Probabilidad (0 a 1) de que cada petición responda con `ErrorStatusCode`. |
| `ErrorStatusCode` | Código 5xx de los errores intermitentes (503 por defecto). |
| `SessionExpiryEveryNth` | Cada N-ésima petición autenticada encuentra su sesión expirada (401, código 301) y la pierde, así que hay que iniciar sesión de nuevo. |

La latencia se aplica primero; la caída total prevalece sobre los errores intermitentes. `POST /_mock/sessions/expire` sigue sirviendo para expirar todas las sesiones de una vez.

```powershell
# Cada tercera petición responde 502 y cada cuarta autenticada pierde la sesión
Invoke-RestMethod -Method Put -Uri http://localhost:5201/_mock/faults -ContentType application/json -Body '{"ErrorEveryNth":3,"ErrorStatusCode":502,"SessionExpiryEveryNth":4}'
# Caída total y recuperación
Invoke-RestMethod -Method Put -Uri http://localhost:5201/_mock/faults -ContentType application/json -Body '{"Outage":true}'
Invoke-RestMethod -Method Delete -Uri http://localhost:5201/_mock/faults
```

## Datos semilla

- **24 artículos** `A00001` a `A00024` de una ferretería, con precio en CLP en la lista 1 y existencias en el almacén `01`. `A00024` no tiene stock.
- **6 clientes** `C00001` a `C00006` y **1 proveedor** `P00001`, ficticios, con RUT, correo y direcciones de facturación y despacho.
- Sin órdenes. La primera orden recibe `DocEntry` 1 y `DocNum` 1001.

## Comportamiento de negocio

- **Órdenes de venta.** El cliente debe existir, estar activo y no ser proveedor; los artículos deben existir; la cantidad debe ser mayor que cero; el almacén debe existir para el artículo; `NumAtCard` admite hasta 100 caracteres y `Comments` hasta 254. `UnitPrice` y `WarehouseCode` se completan con el precio base y el almacén por defecto. La creación es atómica: si una línea es inválida no se crea nada.
- **Stock.** Una orden de venta **compromete** stock (`Committed`) y no descuenta `InStock`, que baja con la entrega. `QuantityOnStock` es la suma de `InStock`.
- **Cuerpos de solicitud.** Una propiedad inexistente se rechaza con `400`, como hace SAP, en lugar de ignorarse. Lo que el cliente envía en propiedades calculadas (`DocEntry`, `DocNum`, `DocTotal`, `CardName`…) se ignora.
- **Claves.** `Entidad('clave')` admite comillas simples duplicadas (`'O''BRIEN'`).

## Fidelidad al contrato

No hay un SAP real contra el cual contrastar, así que cada comportamiento indica de dónde sale. Antes de usar el hub con un SAP real conviene validar los marcados como *aproximado*.

| Comportamiento | Estado |
|---|---|
| Login: cuerpo `CompanyDB`/`UserName`/`Password`; respuesta con `SessionId`, `Version`, `SessionTimeout` (minutos); cookie `B1SESSION` | Documentado en fuentes públicas |
| Expiración por 30 minutos de inactividad | Documentado en fuentes públicas |
| Sesión inválida o expirada: HTTP `401`, código `301`, mensaje `Invalid session.` | Documentado en fuentes públicas (SAP Community) |
| Formato de error `{"error":{"code":…,"message":{"lang":"en-us","value":"…"}}}` | Documentado en fuentes públicas |
| Paginación: 20 filas por defecto, `Prefer: odata.maxpagesize`, `Preference-Applied`, `odata.nextLink` relativo | Documentado en fuentes públicas |
| Registro inexistente: código `-2028`, "No matching records found (ODBC -2028)" | Código conocido de SAP B1; el estado HTTP `404` es **aproximado** |
| Registro duplicado: código `-2035`, "This entry already exists in the following tables (ODBC -2035)" | Código conocido de SAP B1; el estado HTTP `400` es **aproximado** |
| Login fallido: HTTP `401`, código `-304` | **Aproximado** (visto en SAP Community, en un contexto distinto) |
| Validación de negocio: código `-10` y sus mensajes | **Aproximado**: los mensajes son propios |
| Propiedad inexistente en el cuerpo: código `-1000`, `Property 'X' of 'Entidad' is invalid` | **Aproximado** |
| Solicitud o consulta mal formada: código `-1` | **Aproximado** |
| Modelo de `Items`, `BusinessPartners` y `Orders` | Subconjunto de las propiedades reales, con sus nombres |

## Límites conocidos

- El `Service Layer v1` es OData v3, obsoleto según SAP desde el Feature Pack 2405 ([ADR 0005](adr/0005-sesion-del-service-layer.md)).
- Las órdenes no calculan impuestos: `DocTotal` es la suma de las líneas.
- No hay `PATCH` ni `DELETE` de artículos y órdenes, ni las acciones `Cancel` y `Close`, ni `$batch`, ni campos de usuario (`U_`).
- Un solo almacén por artículo y una sola lista de precios en los datos semilla.
- `$filter` cubre el subconjunto indicado; no hay funciones de fecha ni navegación entre entidades.
