<#
.SYNOPSIS
    Repite tests dentro de un contenedor Linux con CPU limitada y hilos ocupados.

.DESCRIPTION
    Una prueba inestable casi nunca falla en una máquina holgada. Este script reproduce las condiciones de un runner
    de CI exigente: el contenedor del SDK de .NET con --cpus limitado y varios hilos consumiendo CPU, y repite el filtro
    las veces que se pida. Termina con código distinto de cero si alguna repetición falla.

.PARAMETER Filter
    Expresión de filtro de "dotnet test --filter", por ejemplo "FullyQualifiedName~CircuitBreaker".

.PARAMETER Reps
    Cantidad de repeticiones (50 por defecto).

.PARAMETER Cpus
    Límite de CPU del contenedor (1 por defecto).

.PARAMETER Burners
    Hilos que consumen CPU durante las repeticiones (4 por defecto).

.PARAMETER Project
    Proyecto de tests (el de integración por defecto).

.EXAMPLE
    ./scripts/repetir-tests.ps1 -Filter "FullyQualifiedName~JobQueueRetryTests" -Reps 20
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [int]$Reps = 50,
    [double]$Cpus = 1,
    [int]$Burners = 4,
    [string]$Project = 'tests/ErpSyncHub.IntegrationTests'
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
    '--cpus', "$Cpus",
    '-v', "${repo}:/src:ro",
    '-v', 'erp-sync-hub-nuget:/root/.nuget/packages',
    '-v', '/var/run/docker.sock:/var/run/docker.sock',
    '--add-host', 'host.docker.internal:host-gateway',
    '-e', 'TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal',
    '-e', 'DOTNET_NOLOGO=true',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=true',
    '-e', "REPS=$Reps",
    '-e', "BURNERS=$Burners",
    '-e', "TEST_PROJECT=$Project",
    '-e', "TEST_FILTER=$Filter",
    $image,
    'bash', '/src/scripts/repetir-test-container.sh'
)

# Ver test.ps1: con ErrorActionPreference=Stop la primera línea en stderr de un comando nativo abortaría el script.
$ErrorActionPreference = 'Continue'
docker @dockerArgs 2>&1 | ForEach-Object { $_.ToString() }
exit $LASTEXITCODE
