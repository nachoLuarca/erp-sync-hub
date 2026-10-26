# Flujo de stock y precios: SAP → tienda

Cómo viajan el stock y los precios de SAP Business One hasta WooCommerce. Las decisiones de fondo están en el [ADR 0009](adr/0009-sincronizacion-de-stock-y-precios.md).

## Secuencia de una ejecución

```mermaid
sequenceDiagram
    autonumber
    participant T as Temporizador / Panel
    participant R as InventorySyncRunner
    participant DB as PostgreSQL
    participant S as SAP (Service Layer)
    participant W as WooCommerce

    T->>R: dispara (cada 15 min o POST /admin/sync/inventory)
    R->>DB: pg_try_advisory_lock (conexión propia)
    alt otra ejecución tiene el lock
        R-->>T: se omite (409 en el panel)
    else lock tomado
        R->>DB: cierra como abandonadas las ejecuciones huérfanas
        R->>DB: INSERT inventory_sync_runs (en curso)
        loop por cada página de artículos, ordenada por ItemCode
            R->>S: GET Items?$filter=UpdateDate ge '…' and ItemCode gt '…'&$top=100
            R->>DB: mapeos SKU ↔ ItemCode y fotos de esos SKU
            R->>R: stock disponible y precio; descarta lo que no cambió
            R->>W: GET products?sku=A,B,… (solo los que hay que enviar)
            R->>W: POST products/batch (hasta 100 por lote)
            R->>DB: guarda la foto de lo que la tienda confirmó
        end
        R->>DB: cierra la ejecución con sus contadores
    end
```

## Qué se calcula

| Dato | Regla |
|---|---|
| **Stock** | Suma de *en mano − comprometido* de los almacenes de `Sync__Inventory__Warehouses`, con tope en 0 y redondeado hacia abajo. Artículo inhabilitado o congelado: 0 (no se despublica). |
| **Precio** | Lista `Sync__Inventory__PriceList`, redondeado a `Sync__Inventory__PriceDecimals`. Nulo o ≤ 0: no se envía (advertencia) y el stock sí. Se escribe el precio regular. |
| **SKU** | Mapeos de `product_mappings`; sin mapeo, el `ItemCode` es el SKU si existe en la tienda (y se guarda el mapeo). |

## Qué pasa con cada artículo leído

Cada artículo cae en exactamente una categoría, que se cuenta en la ejecución:

| Contador | Significa |
|---|---|
| `updated` | La tienda confirmó el cambio de stock o de precio. |
| `unchanged` | Coincide con la última foto confirmada: no se envió nada. |
| `skipped` | No se puede sincronizar por sus datos: no hay producto con ese SKU en la tienda (el hub no crea productos) o no tiene precio válido y nada más que enviar. |
| `failed` | La tienda rechazó el producto o no se pudo enviar. Se reintenta en la siguiente ejecución. |

Un artículo con varios SKU toma el resultado más grave.

## Estados de una ejecución

| Estado | Significa |
|---|---|
| `running` | En curso. |
| `succeeded` | Terminó sin errores. Es la única que avanza la fecha de corte de la siguiente lectura. |
| `completed_with_errors` | Terminó, pero hubo artículos con error. |
| `failed` | Se interrumpió por un error general (SAP o la tienda inaccesibles, credenciales rechazadas) o porque el hub se detenía. |
| `abandoned` | La instancia que la ejecutaba cayó; la cerró la siguiente ejecución. |

## Panel

Sin autenticación hasta la Fase 4: no expongas el hub fuera de una red de confianza.

| Endpoint | Para qué sirve |
|---|---|
| `POST /admin/sync/inventory` | Ejecuta una sincronización ahora y responde con su registro. `?full=true` lee todo el maestro y envía todo, ignorando la foto (por ejemplo, tras editar stock directamente en la tienda). 409 si ya hay una en curso o la tienda no está configurada. Responde 200 aunque la ejecución haya terminado con errores: revisa `status`. |
| `GET /admin/sync/inventory/last` | La última ejecución; 404 si nunca hubo una. |
| `GET /admin/sync/inventory/runs?limit=20` | Las últimas ejecuciones (1 a 100), de la más reciente a la más antigua. |

Ejemplo de respuesta:

```json
{
  "id": "019a2f6e-3c1a-7b52-9d0e-6f1f3a8b2c40",
  "trigger": "manual",
  "status": "succeeded",
  "full": false,
  "changedSince": "2026-10-25",
  "startedAt": "2026-10-26T15:02:11.482+00:00",
  "finishedAt": "2026-10-26T15:02:12.094+00:00",
  "durationSeconds": 0.612,
  "instance": "hub-1",
  "items": { "read": 24, "updated": 2, "unchanged": 0, "skipped": 22, "failed": 0 },
  "error": null
}
```

## Logs y correlación

Todos los logs de una ejecución llevan la propiedad `InventoryRunId`, igual al `id` del registro. En Seq (`http://localhost:5202`), el filtro `InventoryRunId = '019a2f6e-3c1a-7b52-9d0e-6f1f3a8b2c40'` muestra el inicio, las advertencias agrupadas y el resumen final. Las advertencias traen los primeros 20 SKU o códigos afectados:

- **Artículos sin producto en la tienda**: crea el producto con ese SKU o define el mapeo en `product_mappings`.
- **Artículos sin precio válido** en la lista configurada.
- **Productos que no se pudieron actualizar**, con el motivo que dio la tienda.

Al arrancar, el hub deja un solo mensaje que dice si la sincronización periódica está activa (con su configuración), desactivada por `Sync__Inventory__Enabled=false`, o **desactivada porque la tienda no está configurada**.

## Configuración

Todas las variables, con su valor por defecto, están en [variables-de-entorno.md](variables-de-entorno.md): `Sync__Inventory__*` para la sincronización y `Store__WooCommerce__*` para la tienda. Las credenciales de la API de WooCommerce se generan como se explica en [credenciales de la API de WooCommerce](variables-de-entorno.md#credenciales-de-la-api-de-woocommerce).

## Límites conocidos

- Solo productos simples: las variaciones de un producto variable no se actualizan.
- Un SKU con coma no se puede buscar en la tienda.
- Si se edita stock o precio a mano en la tienda y SAP no cambia, la foto no lo detecta: usa `full=true`.
- Hay que validar con un SAP real que `UpdateDate` cambie también con los movimientos de inventario.
