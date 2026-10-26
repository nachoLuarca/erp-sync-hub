<#
.SYNOPSIS
    Prueba de punta a punta de la sincronización de stock y precios: SAP simulado → hub → WooCommerce real.

.DESCRIPTION
    Requiere el entorno levantado con la tienda y las credenciales de la API de WooCommerce definidas:

        docker compose --profile tienda up -d --build

    (variables ERPSYNC_POSTGRES_PASSWORD, ERPSYNC_WC_WEBHOOK_SECRET, ERPSYNC_WP_DB_PASSWORD, ERPSYNC_WP_ADMIN_PASSWORD,
    ERPSYNC_WC_CONSUMER_KEY, ERPSYNC_WC_CONSUMER_SECRET y ERPSYNC_ADMIN_API_KEY; ver docs/variables-de-entorno.md).

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

Write-Host 'Restaurando el SAP simulado...'
Invoke-RestMethod -Method Post -Uri "$MockUrl/_mock/reset" | Out-Null

if ($failures.Count -gt 0) {
    Write-Host "`nFallaron $($failures.Count) comprobaciones: $($failures -join '; ')" -ForegroundColor Red
    exit 1
}

Write-Host "`nPrueba de punta a punta correcta." -ForegroundColor Green
