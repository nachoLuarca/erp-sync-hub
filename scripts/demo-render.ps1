<#
.SYNOPSIS
    Demo de punta a punta contra el hub y el SAP simulado desplegados (por ejemplo, en Render), sin WooCommerce.

.DESCRIPTION
    Hace de tienda: envía al webhook del hub un pedido firmado con HMAC-SHA256, tal como lo haría WooCommerce, y muestra
    qué pasó después:

      1. Despierta el hub y el SAP simulado (en el plan gratuito de Render se duermen sin tráfico y tardan hasta un minuto).
      2. Envía el pedido firmado y comprueba que el hub lo acepta (202).
      3. Consulta el panel (/admin/jobs) hasta que el job termina y muestra sus intentos.
      4. Consulta el SAP simulado y muestra la Orden de Venta que quedó creada.

    Solo lee el panel, así que basta una clave de lectura. No toca los endpoints /_mock ni modifica nada más que crear un
    pedido nuevo en cada ejecución (con identificadores únicos). El pedido de ejemplo es ficticio.

    Variables de entorno (ver docs/variables-de-entorno.md):

      ERPSYNC_WC_WEBHOOK_SECRET        Secreto del webhook; el mismo que Webhooks__WooCommerce__Secret del hub desplegado.
      ERPSYNC_ADMIN_READ_API_KEYS      Clave del panel (de lectura u operación; si hay varias, se usa la primera).
                                       Si no está, se usa ERPSYNC_ADMIN_OPERATE_API_KEYS.
      ERPSYNC_SAP_COMPANY_DB           Opcional. Por defecto SBODemoCL.
      ERPSYNC_SAP_USER_NAME            Opcional. Por defecto manager.
      ERPSYNC_SAP_PASSWORD             Contraseña del SAP simulado desplegado (SapMock__Password). Por defecto manager.
      ERPSYNC_DEMO_HUB_URL             Dirección del hub, si no se pasa -HubUrl.
      ERPSYNC_DEMO_MOCK_URL            Dirección del SAP simulado, si no se pasa -MockUrl.

    Sale con código distinto de cero si algo falla.

.PARAMETER HubUrl
    Dirección del hub desplegado, por ejemplo https://erp-sync-hub.onrender.com.

.PARAMETER MockUrl
    Dirección del SAP simulado desplegado, por ejemplo https://sap-mock.onrender.com.

.PARAMETER TimeoutSeconds
    Cuánto esperar a que cada servicio despierte y a que el job termine.

.EXAMPLE
    ./scripts/demo-render.ps1 -HubUrl https://erp-sync-hub.onrender.com -MockUrl https://sap-mock.onrender.com
#>
[CmdletBinding()]
param(
    [string]$HubUrl = $env:ERPSYNC_DEMO_HUB_URL,
    [string]$MockUrl = $env:ERPSYNC_DEMO_MOCK_URL,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 no negocia TLS 1.2 por defecto y los servicios en Render lo exigen.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if (-not $HubUrl -or -not $MockUrl) {
    throw 'Indica -HubUrl y -MockUrl (o define ERPSYNC_DEMO_HUB_URL y ERPSYNC_DEMO_MOCK_URL).'
}
$HubUrl = $HubUrl.TrimEnd('/')
$MockUrl = $MockUrl.TrimEnd('/')

$webhookSecret = $env:ERPSYNC_WC_WEBHOOK_SECRET
if (-not $webhookSecret) { throw 'Define ERPSYNC_WC_WEBHOOK_SECRET (ver docs/variables-de-entorno.md).' }

$adminKeys = if ($env:ERPSYNC_ADMIN_READ_API_KEYS) { $env:ERPSYNC_ADMIN_READ_API_KEYS } else { $env:ERPSYNC_ADMIN_OPERATE_API_KEYS }
$adminKey = ("$adminKeys" -split ',')[0].Trim()
if (-not $adminKey) { throw 'Define ERPSYNC_ADMIN_READ_API_KEYS o ERPSYNC_ADMIN_OPERATE_API_KEYS (ver docs/variables-de-entorno.md).' }

function Get-EnvOrDefault([string]$name, [string]$default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { $default } else { $value }
}

$sapLogin = @{
    CompanyDB = Get-EnvOrDefault 'ERPSYNC_SAP_COMPANY_DB' 'SBODemoCL'
    UserName  = Get-EnvOrDefault 'ERPSYNC_SAP_USER_NAME' 'manager'
    Password  = Get-EnvOrDefault 'ERPSYNC_SAP_PASSWORD' 'manager'
} | ConvertTo-Json

$hubHeaders = @{ 'X-Api-Key' = $adminKey }

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Wait-ForHealth([string]$name, [string]$url) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $started = [DateTime]::UtcNow
    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri "$url/health" -UseBasicParsing -TimeoutSec 20
            if ($response.StatusCode -eq 200) {
                Write-Host ("   {0} responde en {1:n0} s" -f $name, ([DateTime]::UtcNow - $started).TotalSeconds)
                return
            }
        }
        catch {
            # Dormido o arrancando: Render responde 5xx o corta la conexión hasta que el contenedor está listo.
        }

        if ([DateTime]::UtcNow -gt $deadline) {
            throw "$name no respondió en /health tras $TimeoutSeconds s ($url)."
        }
        Write-Host "   esperando a $name (puede tardar hasta un minuto si estaba dormido)..."
        Start-Sleep -Seconds 5
    }
}

Write-Step '1. Despertar los servicios'
Wait-ForHealth 'SAP simulado' $MockUrl
Wait-ForHealth 'Hub' $HubUrl

Write-Step '2. Enviar un pedido firmado, como lo haría WooCommerce'
# Cada ejecución usa un pedido y un cliente nuevos: así el hub crea un cliente y una orden distintos y no hay idempotencia que lo oculte.
$orderId = [int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 1000000) + 100000
$fixture = Join-Path $PSScriptRoot '..\tests\Fixtures\woocommerce\order-processing.json'
$orderJson = (Get-Content $fixture -Raw -Encoding UTF8) `
    -replace '"id": 5501,', "`"id`": $orderId," `
    -replace '"number": "5501"', "`"number`": `"$orderId`"" `
    -replace '"customer_id": 4521', "`"customer_id`": $orderId"
$body = [Text.Encoding]::UTF8.GetBytes($orderJson)
$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($webhookSecret))
$signature = [Convert]::ToBase64String($hmac.ComputeHash($body))

$webhook = Invoke-WebRequest -Method Post -Uri "$HubUrl/webhooks/woocommerce/orders" -Body $body -UseBasicParsing `
    -ContentType 'application/json' -TimeoutSec 60 -Headers @{
    'X-WC-Webhook-Signature'   = $signature
    'X-WC-Webhook-Topic'       = 'order.created'
    'X-WC-Webhook-Delivery-ID' = [guid]::NewGuid().ToString('N')
}
if ($webhook.StatusCode -ne 202) { throw "El webhook respondió $($webhook.StatusCode); se esperaba 202." }
Write-Host "   pedido $orderId aceptado por el hub (202), firma HMAC-SHA256 verificada"

Write-Step '3. El job en el panel del hub'
function Get-Job { @((Invoke-RestMethod -Uri "$HubUrl/admin/jobs?externalId=$orderId" -Headers $hubHeaders -TimeoutSec 60).items)[0] }

$terminal = 'succeeded', 'failed', 'dead_letter'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$job = $null
while ($true) {
    $job = Get-Job
    if ($job -and $terminal -contains $job.status) { break }
    if ([DateTime]::UtcNow -gt $deadline) {
        $last = if ($job) { $job.status } else { 'sin job' }
        throw "El job del pedido $orderId no terminó en $TimeoutSeconds s (estado: $last)."
    }
    Start-Sleep -Seconds 2
}

$detail = Invoke-RestMethod -Uri "$HubUrl/admin/jobs/$($job.id)" -Headers $hubHeaders -TimeoutSec 60
Write-Host "   job:       $($job.id)"
Write-Host "   estado:    $($job.status)"
Write-Host "   intentos:  $(@($detail.attempts).Count)"
foreach ($attempt in @($detail.attempts)) {
    $outcome = "{0}: {1}" -f $attempt.number, $attempt.outcome
    if ($attempt.error) { $outcome += " ($($attempt.error.code))" }
    Write-Host "     intento $outcome"
}
if ($job.status -ne 'succeeded') {
    throw "El job terminó en '$($job.status)'. Revisa $HubUrl/admin/jobs/$($job.id) con tu clave del panel."
}

Write-Step '4. La Orden de Venta en el SAP simulado'
Invoke-RestMethod -Method Post -Uri "$MockUrl/b1s/v1/Login" -ContentType 'application/json' -Body $sapLogin `
    -SessionVariable sapSession -TimeoutSec 60 | Out-Null
$filter = [Uri]::EscapeDataString("NumAtCard eq 'WC-$orderId'")
$orders = @((Invoke-RestMethod -Uri "$MockUrl/b1s/v1/Orders?`$filter=$filter" -WebSession $sapSession -TimeoutSec 60).value)
if ($orders.Count -ne 1) { throw "Se esperaba 1 orden de venta para WC-$orderId y hay $($orders.Count)." }

$order = $orders[0]
Write-Host "   DocEntry:   $($order.DocEntry)"
Write-Host "   NumAtCard:  $($order.NumAtCard)"
Write-Host "   CardCode:   $($order.CardCode)"
Write-Host "   DocTotal:   $($order.DocTotal)"
foreach ($line in @($order.DocumentLines)) {
    Write-Host "   línea:      $($line.ItemCode) x $($line.Quantity)"
}

Write-Step '5. Métricas del panel'
$metrics = Invoke-RestMethod -Uri "$HubUrl/admin/metrics" -Headers $hubHeaders -TimeoutSec 60
Write-Host ('   jobs por estado: ' + (($metrics.jobs.byStatus.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '))

Write-Host ''
Write-Host "Listo: el pedido $orderId llegó al hub, se procesó y quedó como la orden $($order.DocEntry) en SAP." -ForegroundColor Green
Write-Host 'Nota: el SAP simulado guarda su estado en memoria; si Render lo duerme o lo reinicia, sus datos vuelven a la semilla.'
