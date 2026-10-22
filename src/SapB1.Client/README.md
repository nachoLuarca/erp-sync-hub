# SapB1.Client

Cliente tipado de .NET para el **Service Layer de SAP Business One** (v1). Inicia sesión por sí solo, renueva la sesión cuando SAP la da por expirada y expone artículos, socios de negocios y órdenes de venta.

No depende de ningún otro proyecto de este repositorio, por lo que puede usarse de forma independiente. Se publicará en NuGet en la Fase 5.

## Configuración

```csharp
builder.Services.AddSapB1Client(builder.Configuration);   // lee la sección "SAP"
```

```json
{
  "SAP": {
    "BaseUrl": "https://sap.empresa.cl:50000",
    "CompanyDb": "SBO_EMPRESA",
    "UserName": "integracion",
    "Password": "",
    "RequestTimeout": "00:00:30",
    "PageSize": 100
  }
}
```

La contraseña **no** debe ir en el archivo: use variables de entorno (`SAP__Password`) o un gestor de secretos. `BaseUrl` es la dirección del servidor sin la ruta del servicio; el cliente agrega `/b1s/v1/`. La configuración se valida al iniciar la aplicación.

También puede configurarse por código con `AddSapB1Client(options => { ... })`. El método devuelve el `IHttpClientBuilder`, para agregar handlers o políticas de resiliencia.

## Uso

```csharp
public class PedidosService(ISapB1Client sap)
{
    public async Task<Order> EnviarAsync(string pedidoWeb, string rut, CancellationToken ct)
    {
        // Idempotencia: si el pedido ya existe en SAP, no se crea otro.
        await foreach (var existente in sap.Orders.QueryAsync(
            new SapQuery { Filter = SapFilter.Equal("NumAtCard", pedidoWeb), Top = 1 }, ct))
        {
            return existente;
        }

        // Cliente por RUT; se crea si no existe.
        BusinessPartner? cliente = null;
        await foreach (var c in sap.BusinessPartners.QueryAsync(
            new SapQuery { Filter = SapFilter.Equal("FederalTaxID", rut), Top = 1 }, ct))
        {
            cliente = c;
        }

        cliente ??= await sap.BusinessPartners.CreateAsync(
            new CreateBusinessPartnerRequest { CardName = "Valentina Muñoz", FederalTaxID = rut }, ct);

        return await sap.Orders.CreateAsync(new CreateOrderRequest
        {
            CardCode = cliente.CardCode,
            NumAtCard = pedidoWeb,
            DocumentLines = [new CreateOrderLine { ItemCode = "A00001", Quantity = 2 }],
        }, ct);
    }
}
```

| Recurso | Operaciones |
|---|---|
| `Items` | `GetAsync`, `QueryAsync` |
| `BusinessPartners` | `GetAsync`, `QueryAsync`, `CreateAsync`, `UpdateAsync` |
| `Orders` | `GetAsync`, `QueryAsync`, `CreateAsync` |

- `GetAsync` devuelve `null` si el registro no existe.
- `QueryAsync` devuelve un `IAsyncEnumerable<T>` y sigue `odata.nextLink` a medida que se enumera; `SapQuery` admite `Filter`, `Select`, `OrderBy`, `Top` y `Skip`.
- **Escape los valores de los filtros con `SapFilter`** (`SapFilter.Equal`, `SapFilter.Quote`); concatenar texto de usuario en un `$filter` permite alterar la consulta.
- Si una consulta usa `Select`, las propiedades no pedidas quedan con su valor por defecto.
- Los nombres de propiedad de SAP distinguen mayúsculas y minúsculas; los modelos ya usan los nombres correctos.

## Sesión

El cliente mantiene una sola sesión por proceso. La abre en la primera llamada y, si SAP responde `401`, inicia otra y reintenta la petición una vez, incluso un `POST` (SAP valida la sesión antes de procesar, así que no hay duplicados). Con muchas llamadas concurrentes sobre una sesión expirada se realiza un único login. Detalles en el [ADR 0005](../../docs/adr/0005-sesion-del-service-layer.md).

`LogoutAsync` cierra la sesión explícitamente; es opcional.

## Errores

| Excepción | Cuándo |
|---|---|
| `SapB1ApiException` | SAP rechazó la operación. Trae `StatusCode`, `SapErrorCode` y `SapErrorMessage`, además de `IsNotFound` e `IsAlreadyExists`. |
| `SapB1SessionException` | No se pudo iniciar sesión, o SAP siguió rechazándola tras renovarla. |

Los fallos de red, los tiempos de espera y la cancelación no se envuelven: llegan como `HttpRequestException`, `TaskCanceledException` u `OperationCanceledException`. Motivos de este diseño en el [ADR 0006](../../docs/adr/0006-excepciones-tipadas-en-sapb1-client.md).

> Si agrega reintentos automáticos con `AddHttpClient(...).AddResilienceHandler(...)`, no reintente los `POST` sin una clave de idempotencia: crear dos veces una orden es un error de negocio. Para `Orders.CreateAsync`, detecte duplicados por `NumAtCard` como se muestra arriba.

## Límites

- Service Layer **v1** (OData v3). SAP lo marca como obsoleto desde el Feature Pack 2405 a favor de v2.
- Una instancia del cliente usa un único servidor, sociedad y usuario.
- `UpdateAsync` no puede borrar un campo: una propiedad en `null` no se envía.
- Alcance: `Items`, `BusinessPartners` y `Orders`. Sin cancelación de órdenes, `$batch` ni campos de usuario.
- Probado contra [SapB1.Mock](../../docs/sap-mock.md); validar contra un SAP real antes de producción.
