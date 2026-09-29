<#
Compila el instalador de NoteChr.

Requisitos: WiX 5 como herramienta de .NET
    dotnet tool install --global wix --version 5.0.2

Uso (desde la raiz del repositorio):
    .\installer\build-installer.ps1

Genera: installer\NoteChr-<version>.msi
#>

param(
    [switch]$Lightweight
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installerDir = $PSScriptRoot
$publishDir = Join-Path $root 'publish\sf-win-x64'

if ($Lightweight) {
    $lightDir = Join-Path $root 'publish\lightweight'
    Write-Host '==> Publicando ejecutable ligero (requiere .NET 8 Desktop Runtime)' -ForegroundColor Cyan
    dotnet publish (Join-Path $root 'NoteChr.csproj') `
        -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None `
        -o $lightDir -v q --nologo
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $lightDir 'NoteChr.exe'))) {
        throw 'La publicación ligera falló.'
    }
    $exe = Get-Item (Join-Path $lightDir 'NoteChr.exe')
    Write-Host "==> Listo: $($exe.FullName)  ($([math]::Round($exe.Length / 1KB, 1)) KB)" -ForegroundColor Green
    Write-Host 'Requiere: .NET 8 Desktop Runtime para Windows x64.' -ForegroundColor Gray
    exit 0
}
$sourcePath = Join-Path $installerDir 'Product.wxs'
$version = ([xml](Get-Content $sourcePath -Raw)).Wix.Package.Version
if (-not $version) { throw 'No se pudo leer la version de Product.wxs.' }
$msiPath = Join-Path $installerDir ("NoteChr-{0}.msi" -f $version)

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw 'No se encontro WiX 5. Instale la herramienta: dotnet tool install --global wix --version 5.0.2'
}

Write-Host '==> Publicando la aplicacion (autocontenida, un solo archivo)' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'NoteChr.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishDir -v q --nologo

if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $publishDir 'NoteChr.exe'))) {
    throw 'La publicacion fallo: no se genero NoteChr.exe'
}

# La carpeta de salida puede existir de una publicación anterior; copiar los recursos
# explícitamente evita que un build incremental deje el MSI sin los temas JSON.
$themesSource = Join-Path $root 'Themes'
$themesTarget = Join-Path $publishDir 'Themes'
New-Item -ItemType Directory -Path $themesTarget -Force | Out-Null
Copy-Item (Join-Path $themesSource '*.json') $themesTarget -Force

Write-Host '==> Compilando el MSI' -ForegroundColor Cyan

$extensions = wix extension list --global
if ($LASTEXITCODE -ne 0) { throw 'No se pudo consultar las extensiones de WiX.' }
$useUi = $extensions -match '^WixToolset\.UI\.wixext\s'

if ($useUi) {
    wix build -arch x64 -ext WixToolset.UI.wixext -d "PublishDir=$publishDir" `
        -b $installerDir -o $msiPath $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'La compilacion del MSI con asistente fallo.' }
} else {
    Write-Warning "WixToolset.UI.wixext no esta instalada. MSI sin asistente grafico; se puede instalar con msiexec /i $msiPath /qn."
    Write-Host '    Para habilitar el asistente: wix extension add WixToolset.UI.wixext/5.0.2 --global'

    # La variante sin UI se escribe fuera del proyecto y se elimina al terminar.
    $noUiDir = Join-Path ([IO.Path]::GetTempPath()) 'opencode'
    New-Item -ItemType Directory -Path $noUiDir -Force | Out-Null
    $noUi = Join-Path $noUiDir ("NoteChr-noUI-{0}.wxs" -f [guid]::NewGuid().ToString('N'))
    try {
        $source = Get-Content $sourcePath -Raw
        $source = $source -replace '\s+xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui"', ''
        $source = $source -replace '\s*<ui:WixUI Id="WixUI_InstallDir"\s*/>', ''
        [IO.File]::WriteAllText($noUi, $source, (New-Object System.Text.UTF8Encoding $false))
        wix build -arch x64 -b $installerDir -d "PublishDir=$publishDir" -o $msiPath $noUi
        if ($LASTEXITCODE -ne 0) { throw 'La compilacion del MSI sin asistente fallo.' }
    } finally {
        Remove-Item $noUi -ErrorAction SilentlyContinue
    }
}

$msi = Get-Item $msiPath
Write-Host ''
Write-Host "==> Listo: $($msi.FullName)  ($([math]::Round($msi.Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host ''
Write-Host "Instalar:  msiexec /i `"$msiPath`"" -ForegroundColor Gray
if (-not $useUi) { Write-Host '   (sin asistente: utilice /qn para modo silencioso)' -ForegroundColor Gray }
Write-Host "Desinstalar:  msiexec /x `"$msiPath`"" -ForegroundColor Gray
