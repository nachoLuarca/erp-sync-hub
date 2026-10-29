<#
.SYNOPSIS
    Verifica el contenido del paquete NuGet de SapB1.Client generado por "dotnet pack".

.DESCRIPTION
    Comprueba, sobre el .nupkg y el .snupkg, lo que un consumidor de NuGet espera encontrar y lo que un empaquetado descuidado suele
    dejar fuera: identidad y versión, licencia, README, repositorio con el commit, el aviso de no afiliación con SAP, dependencias
    que no sean del hub, el ensamblado y su documentación XML, y los símbolos con SourceLink. Sale con código distinto de cero si
    alguna comprobación falla. Lo usan el workflow de release y la verificación local.

.PARAMETER Directory
    Carpeta donde "dotnet pack" dejó los paquetes.

.PARAMETER Version
    Versión esperada del paquete (la del tag, sin la "v"). Sin valor, se acepta la que traiga el paquete.

.EXAMPLE
    dotnet pack src/SapB1.Client -c Release -o artifacts -p:Version=1.0.0 -p:ContinuousIntegrationBuild=true
    ./scripts/verificar-paquete.ps1 -Directory artifacts -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packageId = 'Luarca.SapB1.Client'
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-That([bool]$condition, [string]$what) {
    if ($condition) { Write-Host "  OK   $what" } else { Write-Host "  FALLA $what" -ForegroundColor Red; $failures.Add($what) }
}

function Open-Zip([string]$path) { [IO.Compression.ZipFile]::OpenRead($path) }

function Read-Entry($zip, [string]$name) {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
    if (-not $entry) { return $null }
    $stream = $entry.Open()
    try {
        $memory = [IO.MemoryStream]::new()
        $stream.CopyTo($memory)
        $memory.ToArray()
    }
    finally { $stream.Dispose() }
}

$nupkgs = @(Get-ChildItem -Path $Directory -Filter "$packageId.*.nupkg" | Where-Object { $_.Name -notlike '*.symbols.nupkg' })
$snupkgs = @(Get-ChildItem -Path $Directory -Filter "$packageId.*.snupkg")
if ($nupkgs.Count -ne 1 -or $snupkgs.Count -ne 1) {
    throw "Se esperaba exactamente un .nupkg y un .snupkg de $packageId en '$Directory' (hay $($nupkgs.Count) y $($snupkgs.Count))."
}

Write-Host "Paquete: $($nupkgs[0].Name)"
$zip = Open-Zip $nupkgs[0].FullName
try {
    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    $nuspec = [xml]::new()
    $nuspec.Load([IO.MemoryStream]::new((Read-Entry $zip $nuspecEntry.FullName)))
    $meta = $nuspec.package.metadata

    Write-Host 'Metadatos'
    Assert-That ($meta.id -eq $packageId) "id es $packageId"
    if ($Version) { Assert-That ($meta.version -eq $Version) "versión es $Version (es $($meta.version))" }
    Assert-That ($meta.version -notlike '0.0.0*') 'la versión no es la provisoria de empaquetado local'
    Assert-That ($meta.license.InnerText -eq 'MIT' -and $meta.license.type -eq 'expression') 'licencia MIT como expresión SPDX'
    Assert-That ($meta.readme -eq 'README.md') 'README declarado en el nuspec'
    Assert-That ($meta.authors -like '*Ignacio Luarca*') 'autor'
    Assert-That (-not [string]::IsNullOrWhiteSpace($meta.description)) 'descripción'
    Assert-That ($meta.description -match 'no está afiliado ni respaldado por SAP SE') 'la descripción aclara que no está afiliado a SAP SE'
    Assert-That ($meta.tags -match 'sap-business-one') 'tags'
    Assert-That ($meta.projectUrl -eq 'https://github.com/nachoLuarca/erp-sync-hub') 'URL del proyecto'
    Assert-That ($meta.repository.type -eq 'git' -and $meta.repository.url -like 'https://github.com/nachoLuarca/erp-sync-hub*') 'repositorio git'
    Assert-That ($meta.repository.commit -match '^[0-9a-f]{40}$') 'el repositorio declara el commit (SourceLink)'

    Write-Host 'Dependencias'
    $dependencies = @($meta.dependencies.group.dependency | ForEach-Object { $_.id })
    Assert-That ($dependencies.Count -gt 0) 'declara dependencias'
    Assert-That (@($dependencies | Where-Object { $_ -notlike 'Microsoft.Extensions.*' }).Count -eq 0) 'solo depende de Microsoft.Extensions.*'
    Assert-That (@($dependencies | Where-Object { $_ -like 'ErpSyncHub*' }).Count -eq 0) 'no depende de nada del hub'
    Assert-That ($meta.dependencies.group.targetFramework -eq 'net10.0') 'apunta a net10.0'

    Write-Host 'Contenido'
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    Assert-That ($names -contains 'lib/net10.0/SapB1.Client.dll') 'ensamblado SapB1.Client.dll'
    Assert-That ($names -contains 'lib/net10.0/SapB1.Client.xml') 'documentación XML'
    Assert-That ($names -contains 'README.md') 'README.md en la raíz'
    Assert-That (-not ($names | Where-Object { $_ -like '*.pdb' })) 'los símbolos no van en el .nupkg'
    $readme = [Text.Encoding]::UTF8.GetString((Read-Entry $zip 'README.md'))
    Assert-That ($readme -match 'no está afiliado, patrocinado ni respaldado por SAP SE') 'el README incluye el aviso de no afiliación'
    Assert-That ($readme -match 'marcas registradas de SAP SE') 'el README atribuye las marcas a SAP SE'
    Assert-That ($readme -notmatch '\]\(\.\./') 'el README no tiene enlaces relativos que NuGet no puede resolver'
}
finally { $zip.Dispose() }

Write-Host "Símbolos: $($snupkgs[0].Name)"
$symbols = Open-Zip $snupkgs[0].FullName
try {
    $pdbEntry = $symbols.Entries | Where-Object { $_.FullName -eq 'lib/net10.0/SapB1.Client.pdb' } | Select-Object -First 1
    Assert-That ($null -ne $pdbEntry) 'el .snupkg trae SapB1.Client.pdb'
    if ($pdbEntry) {
        $pdb = Read-Entry $symbols $pdbEntry.FullName
        $text = [Text.Encoding]::ASCII.GetString($pdb)
        Assert-That ($text.Contains('raw.githubusercontent.com/nachoLuarca/erp-sync-hub/')) 'el PDB trae SourceLink hacia el repositorio'
    }
}
finally { $symbols.Dispose() }

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "El paquete tiene $($failures.Count) problema(s)." -ForegroundColor Red
    exit 1
}
Write-Host 'El paquete cumple todas las comprobaciones.' -ForegroundColor Green
