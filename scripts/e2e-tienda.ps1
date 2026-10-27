<#
.SYNOPSIS
    Prueba de punta a punta de la sincronización de stock y precios: SAP simulado → hub → WooCommerce real.

.DESCRIPTION
    Requiere el entorno levantado con la tienda y las credenciales de la API de WooCommerce definidas:

        docker compose --profile tienda up -d --build

    (variables ERPSYNC_POSTGRES_PASSWORD, ERPSYNC_WC_WEBHOOK_SECRET, ERPSYNC_WP_DB_PASSWORD, ERPSYNC_WP_ADMIN_PASSWORD,
    ERPSYNC_WC_CONSUMER_KEY, ERPSYNC_WC_CONSUMER_SECRET y ERPSYNC_ADMIN_API_KEY; ver docs/variables-de-entorno.md).

    Además de la sincronización de stock y precios, comprueba la resiliencia: simula una caída de SAP, envía un pedido firmado
    como lo haría WooCommerce, lo reintenta desde el panel al volver SAP y verifica que queda una sola orden, que las métricas y
    el control de acceso del panel responden como corresponde y que los datos personales del pedido no llegaron a Seq.

    El script cambia stock y precios en el SAP simulado, dispara la sincronización por el panel y comprueba qué quedó en la
    tienda. Restaura los datos del SAP simulado al terminar. No toca nada más.

    Sale con código distinto de cero si alguna comprobación falla.

.PARAMETER HubUrl
    Dirección del hub. Por defecto, el puerto ERPSYNC_HUB_HOST_PORT o 5200.

.PARAMETER MockUrl
    Dirección del SAP simulado. Por defecto, el puerto ERPSYNC_SAP_MOCK_HOST_PORT o 5201.

.PARAMETER StoreUrl
    Dirección de la tienda. Por defecto, el puerto ERPSYNC_TIENDA_HOST_PORT o 5203.

.PARAMETER TimeoutSeconds
    Cuánto esperar a que el hub y la tienda estén listos.

.EXAMPLE
    ./scripts/e2e-tienda.ps1
#>
[CmdletBinding()]
param(
    [string]$HubUrl,
    [string]$MockUrl,
    [string]$StoreUrl,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

function Get-Port([string]$name, [int]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { $default } else { [int]$value }
}

if (-not $HubUrl) { $HubUrl = "http://127.0.0.1:$(Get-Port 'ERPSYNC_HUB_HOST_PORT' 5200)" }
if (-not $MockUrl) { $MockUrl = "http://127.0.0.1:$(Get-Port 'ERPSYNC_SAP_MOCK_HOST_PORT' 5201)" }
if (-not $StoreUrl) { $StoreUrl = "http://127.0.0.1:$(Get-Port 'ERPSYNC_TIENDA_HOST_PORT' 5203)" }

$key = $env:ERPSYNC_WC_CONSUMER_KEY
$secret = $env:ERPSYNC_WC_CONSUMER_SECRET
if (-not $key -or -not $secret) {
    throw 'Define ERPSYNC_WC_CONSUMER_KEY y ERPSYNC_WC_CONSUMER_SECRET (ver docs/variables-de-entorno.md).'
}

# El panel del hub exige una API key; si hay varias separadas por coma, basta la primera.
$adminKey = ("$env:ERPSYNC_ADMIN_API_KEY" -split ',')[0].Trim()
if (-not $adminKey) {
    throw 'Define ERPSYNC_ADMIN_API_KEY (ver docs/variables-de-entorno.md).'
}

$hubHeaders = @{ 'X-Api-Key' = $adminKey }
$auth = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${key}:${secret}")) }
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Equal($actual, $expected, [string]$what) {
    if ("$actual" -eq "$expected") {
        Write-Host "  OK    $what = $actual"
    }
    else {
        Write-Host "  FALLA ${what}: se esperaba '$expected' y fue '$actual'" -ForegroundColor Red
        $failures.Add($what)
    }
}

function Wait-Until([scriptblock]$condition, [string]$description) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return } } catch { }
        Start-Sleep -Seconds 3
    }
    throw "Se agotó el tiempo esperando: $description."
}

function Get-StoreProduct([string]$sku) {
    $found = Invoke-RestMethod -Uri "$StoreUrl/wp-json/wc/v3/products?sku=$sku&_fields=id,sku,status,stock_quantity,stock_status,regular_price" -Headers $auth
    @($found)[0]
}

function Set-MockItem([string]$itemCode, [hashtable]$changes) {
    Invoke-RestMethod -Method Patch -Uri "$MockUrl/_mock/items/$itemCode" -ContentType 'application/json' -Body ($changes | ConvertTo-Json) | Out-Null
}

function Invoke-Sync([switch]$Full) {
    $query = if ($Full) { '?full=true' } else { '' }
    Invoke-RestMethod -Method Post -Uri "$HubUrl/admin/sync/inventory$query" -Headers $hubHeaders
}

$startedAt = [DateTime]::UtcNow
Write-Host "Esperando al hub ($HubUrl) y a la tienda ($StoreUrl)..."
Wait-Until { (Invoke-WebRequest -Uri "$HubUrl/health" -UseBasicParsing).StatusCode -eq 200 } 'que el hub responda en /health'
Wait-Until { (Get-StoreProduct 'A00001').sku -eq 'A00001' } 'que la tienda acepte las credenciales y tenga el producto A00001 (¿terminó tienda-setup?)'

Write-Host 'Restaurando el SAP simulado y desordenando la tienda...'
Invoke-RestMethod -Method Post -Uri "$MockUrl/_mock/reset" | Out-Null
foreach ($sku in 'A00001', 'A00002', 'A00010') {
    $id = (Get-StoreProduct $sku).id
    Invoke-RestMethod -Method Put -Uri "$StoreUrl/wp-json/wc/v3/products/$id" -Headers $auth -ContentType 'application/json' `
        -Body (@{ manage_stock = $true; stock_quantity = 1; regular_price = '1' } | ConvertTo-Json) | Out-Null
}

Write-Host '1. Sincronización completa: la tienda queda igual que SAP'
$run = Invoke-Sync -Full
Assert-Equal $run.status 'succeeded' 'estado'
Assert-Equal $run.full $true 'full'
Assert-Equal $run.items.read 24 'artículos leídos'
Assert-Equal $run.items.updated 3 'actualizados (los tres con producto en la tienda)'
Assert-Equal $run.items.skipped 21 'omitidos (sin producto en la tienda)'
$drill = Get-StoreProduct 'A00001'
Assert-Equal $drill.stock_quantity 45 'A00001 stock'
Assert-Equal $drill.regular_price 39990 'A00001 precio'
Assert-Equal (Get-StoreProduct 'A00002').stock_quantity 60 'A00002 stock'
Assert-Equal (Get-StoreProduct 'A00010').regular_price 2990 'A00010 precio'

Write-Host '2. Cambio en SAP: solo se envía lo que cambió (stock disponible = en mano - comprometido)'
Set-MockItem 'A00001' @{ InStock = 20; Committed = 5; Price = 41990 }
$run = Invoke-Sync
Assert-Equal $run.status 'succeeded' 'estado'
Assert-Equal $run.items.updated 1 'actualizados'
Assert-Equal $run.items.unchanged 2 'sin cambios'
$drill = Get-StoreProduct 'A00001'
Assert-Equal $drill.stock_quantity 15 'A00001 stock'
Assert-Equal $drill.regular_price 41990 'A00001 precio'

Write-Host '3. Sin cambios en SAP: no se envía nada'
$run = Invoke-Sync
Assert-Equal $run.items.updated 0 'actualizados'
Assert-Equal $run.items.unchanged 3 'sin cambios'

Write-Host '4. Stock sobrecomprometido: el disponible tiene tope en 0'
Set-MockItem 'A00010' @{ InStock = 3; Committed = 8 }
$run = Invoke-Sync
Assert-Equal $run.items.updated 1 'actualizados'
Assert-Equal (Get-StoreProduct 'A00010').stock_quantity 0 'A00010 stock'
Assert-Equal (Get-StoreProduct 'A00010').stock_status 'outofstock' 'A00010 estado de stock'

Write-Host '5. Artículo congelado: stock 0 sin despublicar'
Set-MockItem 'A00002' @{ Frozen = $true }
$run = Invoke-Sync
Assert-Equal $run.items.updated 1 'actualizados'
$frozen = Get-StoreProduct 'A00002'
Assert-Equal $frozen.stock_quantity 0 'A00002 stock'
Assert-Equal $frozen.status 'publish' 'A00002 sigue publicado'

Write-Host '6. Precio 0 en SAP: no se envía el precio, el stock sí'
Set-MockItem 'A00001' @{ InStock = 30; Committed = 0; Price = 0 }
$run = Invoke-Sync
Assert-Equal $run.items.updated 1 'actualizados'
$drill = Get-StoreProduct 'A00001'
Assert-Equal $drill.stock_quantity 30 'A00001 stock'
Assert-Equal $drill.regular_price 41990 'A00001 conserva el precio anterior'

Write-Host '7. El panel consulta la última ejecución'
$last = Invoke-RestMethod -Uri "$HubUrl/admin/sync/inventory/last" -Headers $hubHeaders
Assert-Equal $last.id $run.id 'la última es la que acaba de correr'
Assert-Equal $last.trigger 'manual' 'disparador'

function Get-StatusCode([scriptblock]$request) {
    try {
        & $request | Out-Null
        200
    }
    catch {
        if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { throw }
    }
}

Write-Host '8. SAP caído: la sincronización de inventario falla con su error y se recupera sola al volver'
Invoke-RestMethod -Method Put -Uri "$MockUrl/_mock/faults" -ContentType 'application/json' -Body '{"Outage":true}' | Out-Null
$down = Invoke-Sync
Assert-Equal $down.status 'failed' 'estado con SAP caído'
Assert-Equal ($down.error.code -like 'sap.*') $true 'el error es de SAP'
Invoke-RestMethod -Method Delete -Uri "$MockUrl/_mock/faults" | Out-Null
Assert-Equal (Invoke-Sync).status 'succeeded' 'estado al volver SAP'

Write-Host '9. Un pedido sobrevive a una caída de SAP y se completa sin duplicar la orden'
$webhookSecret = $env:ERPSYNC_WC_WEBHOOK_SECRET
if (-not $webhookSecret) { throw 'Define ERPSYNC_WC_WEBHOOK_SECRET (ver docs/variables-de-entorno.md).' }
$orderId = [int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 1000000) + 100000
$fixture = Join-Path $PSScriptRoot '..\tests\Fixtures\woocommerce\order-processing.json'
# Cada corrida usa un pedido y un cliente de la tienda nuevos: el hub guarda el mapeo cliente → CardCode, y el reinicio del SAP simulado
# del final borra al cliente de SAP pero no ese mapeo.
$orderJson = (Get-Content $fixture -Raw -Encoding UTF8) `
    -replace '"id": 5501,', "`"id`": $orderId," `
    -replace '"number": "5501"', "`"number`": `"$orderId`"" `
    -replace '"customer_id": 4521', "`"customer_id`": $orderId"
$body = [Text.Encoding]::UTF8.GetBytes($orderJson)
$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($webhookSecret))
$signature = [Convert]::ToBase64String($hmac.ComputeHash($body))

Invoke-RestMethod -Method Put -Uri "$MockUrl/_mock/faults" -ContentType 'application/json' -Body '{"Outage":true}' | Out-Null
$webhook = Invoke-WebRequest -Method Post -Uri "$HubUrl/webhooks/woocommerce/orders" -Body $body -UseBasicParsing -ContentType 'application/json' -Headers @{
    'X-WC-Webhook-Signature'   = $signature
    'X-WC-Webhook-Topic'       = 'order.created'
    'X-WC-Webhook-Delivery-ID' = [guid]::NewGuid().ToString('N')
}
Assert-Equal $webhook.StatusCode 202 'el webhook se acepta aunque SAP esté caído'

function Get-Job { @((Invoke-RestMethod -Uri "$HubUrl/admin/jobs?externalId=$orderId" -Headers $hubHeaders).items)[0] }
Wait-Until { $j = Get-Job; $j -and $j.status -eq 'retrying' } 'que el job quede esperando su reintento'
$job = Get-Job
Assert-Equal ($job.lastError.code.StartsWith('sap.')) $true 'el job guarda el error transitorio de SAP'
Assert-Equal ($null -ne $job.nextAttemptAt) $true 'el job tiene hora de reintento'

Invoke-RestMethod -Method Delete -Uri "$MockUrl/_mock/faults" | Out-Null
$retried = Invoke-RestMethod -Method Post -Uri "$HubUrl/admin/jobs/$($job.id)/retry" -Headers $hubHeaders
Assert-Equal $retried.status 'pending' 'el reintento manual deja el job pendiente'
Wait-Until { (Get-Job).status -eq 'succeeded' } 'que el job termine al volver SAP'

$sapLogin = @{ CompanyDB = $(if ($env:ERPSYNC_SAP_COMPANY_DB) { $env:ERPSYNC_SAP_COMPANY_DB } else { 'SBODemoCL' }); UserName = 'manager'; Password = 'manager' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$MockUrl/b1s/v1/Login" -ContentType 'application/json' -Body $sapLogin -SessionVariable sapSession | Out-Null
$filter = [Uri]::EscapeDataString("NumAtCard eq 'WC-$orderId'")
$orders = Invoke-RestMethod -Uri "$MockUrl/b1s/v1/Orders?`$filter=$filter&`$select=DocEntry" -WebSession $sapSession
Assert-Equal @($orders.value).Count 1 'órdenes de SAP para el pedido (sin duplicar)'
$finished = Invoke-RestMethod -Uri "$HubUrl/admin/jobs/$($job.id)" -Headers $hubHeaders
Assert-Equal (@($finished.attempts | Where-Object { $_.outcome -eq 'failed' }).Count -gt 0) $true 'el historial conserva los intentos fallidos'
Assert-Equal (@($finished.attempts)[-1]).outcome 'succeeded' 'el último intento es el exitoso'
Assert-Equal ($null -eq $finished.payload) $true 'el detalle no trae el payload con datos personales por defecto'

Write-Host '10. Panel: métricas y control de acceso'
$metrics = Invoke-RestMethod -Uri "$HubUrl/admin/metrics" -Headers $hubHeaders
Assert-Equal ($metrics.jobs.byStatus.succeeded -ge 1) $true 'las métricas cuentan el job exitoso'
Assert-Equal ($null -ne $metrics.inventorySync.lastSucceededRun) $true 'las métricas traen la última sincronización de inventario exitosa'
Assert-Equal (Get-StatusCode { Invoke-RestMethod -Uri "$HubUrl/admin/metrics" }) 401 'sin clave el panel responde'
Assert-Equal (Get-StatusCode { Invoke-RestMethod -Uri "$HubUrl/admin/jobs" -Headers @{ 'X-Api-Key' = ('x' * 40) } }) 401 'con una clave incorrecta el panel responde'

Write-Host '11. Los datos personales del pedido no llegan a los logs de Seq'
$seqPort = Get-Port 'ERPSYNC_SEQ_HOST_PORT' 5202
try {
    Start-Sleep -Seconds 6   # Seq recibe los logs por lotes
    # Se revisan los eventos de esta corrida (Seq conserva los de las anteriores), completos: mensaje, propiedades y excepción.
    $from = [Uri]::EscapeDataString($startedAt.ToString('o'))
    $events = Invoke-RestMethod -Uri "http://127.0.0.1:$seqPort/api/events?count=5000&render=true&fromDateUtc=$from" -TimeoutSec 30
    # En Windows PowerShell 5.1 un arreglo JSON no se enumera al asignarlo con @(): se recorre la variable ya asignada.
    $eventsJson = foreach ($event in $events) { $event | ConvertTo-Json -Depth 20 -Compress }
    $eventsJson = @($eventsJson)

    # Control positivo: Seq sí tiene los logs del job; sin esto, «cero resultados» podría significar que la revisión no ve nada.
    $jobEvents = @(foreach ($event in $events) { if ($event.RenderedMessage -like "*Orden * creada en SAP para el pedido $orderId*") { $event } })
    Assert-Equal ($jobEvents.Count -ge 1) $true "Seq tiene los logs del pedido $orderId (control)"
    Assert-Equal (@($events).Count -gt 20) $true "eventos de Seq revisados ($(@($events).Count))"
    foreach ($datum in 'Valentina', 'Muñoz', 'valentina@example.com', '18.765.432-7', '18765432', '+56912345678', 'Av. Providencia', 'Depto 56', '190.96.10.20', 'Entregar en conserjería') {
        $hits = @($eventsJson | Where-Object { $_.IndexOf($datum, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        Assert-Equal $hits.Count 0 "eventos de Seq que contienen '$datum'"
    }
}
catch {
    Write-Host "  AVISO: no se pudo consultar Seq en el puerto ${seqPort}; se omite la comprobación ($($_.Exception.Message))." -ForegroundColor Yellow
}

Write-Host 'Restaurando el SAP simulado...'
Invoke-RestMethod -Method Post -Uri "$MockUrl/_mock/reset" | Out-Null

if ($failures.Count -gt 0) {
    Write-Host "`nFallaron $($failures.Count) comprobaciones: $($failures -join '; ')" -ForegroundColor Red
    exit 1
}

Write-Host "`nPrueba de punta a punta correcta." -ForegroundColor Green
