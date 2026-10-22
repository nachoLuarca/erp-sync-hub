<#
.SYNOPSIS
    Verifica la solución (restore, build, formato y tests) dentro de un contenedor Linux.

.DESCRIPTION
    En Windows con Smart App Control, testhost.exe no puede cargar los ensamblados recién
    compilados. Este script ejecuta la misma verificación que CI en el contenedor oficial del
    SDK de .NET, montando el repositorio como solo lectura y trabajando sobre una copia.
    La caché de NuGet se conserva en el volumen Docker "erp-sync-hub-nuget".

.PARAMETER Filter
    Expresión de filtro de "dotnet test --filter", por ejemplo "FullyQualifiedName~Architecture".

.PARAMETER SkipFormat
    Omite la verificación de "dotnet format".

.EXAMPLE
    ./scripts/test.ps1

.EXAMPLE
    ./scripts/test.ps1 -Filter "FullyQualifiedName~SapB1.Client"
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [switch]$SkipFormat
)

$ErrorActionPreference = 'Stop'

$image = 'mcr.microsoft.com/dotnet/sdk:10.0'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker no está disponible en el PATH. Instálalo o inicia Docker Desktop.'
}

docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'El servicio de Docker no responde. Inicia Docker Desktop e inténtalo de nuevo.'
}

$dockerArgs = @(
    'run', '--rm',
    '-v', "${repo}:/src:ro",
    '-v', 'erp-sync-hub-nuget:/root/.nuget/packages',
    '-e', 'DOTNET_NOLOGO=true',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=true',
    '-e', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true',
    '-e', "SKIP_FORMAT=$(if ($SkipFormat) { '1' } else { '0' })",
    '-e', "TEST_FILTER=$Filter",
    $image,
    'bash', '/src/scripts/test-container.sh'
)

# En Windows PowerShell 5.1, con ErrorActionPreference=Stop la primera línea que un comando nativo escribe
# en stderr (xUnit informa ahí los tests fallidos) aborta el script y trunca la salida.
$ErrorActionPreference = 'Continue'
docker @dockerArgs 2>&1 | ForEach-Object { $_.ToString() }
exit $LASTEXITCODE
