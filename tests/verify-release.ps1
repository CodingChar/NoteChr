$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host '==> Compilando Release'
dotnet build NoteChr.csproj -c Release -v q --nologo

Write-Host '==> Validando temas JSON'
$themeFile = Join-Path $root 'Themes\themes.json'
$themes = Get-Content $themeFile -Raw | ConvertFrom-Json
$required = @('classic', 'dark-modern', 'warm-cozy', 'synthwave', 'minimal-paper', 'pure-oled')
foreach ($id in $required) {
    if (-not @($themes | Where-Object Id -eq $id)) { throw "Falta el tema: $id" }
}
foreach ($theme in $themes) {
    foreach ($property in @('Background','Surface','Border','TextPrimary','TextSecondary','Accent','Selection','EditorBackground','EditorForeground','Caret')) {
        if ($theme.$property -notmatch '^#[0-9A-Fa-f]{6}$') { throw "Color invalido en $($theme.Id): $property" }
    }
}

Write-Host '==> Validando icono y terminos'
if (-not (Test-Path (Join-Path $root 'icon.ico'))) { throw 'Falta icon.ico' }
if (-not (Test-Path (Join-Path $root 'installer\terms.rtf'))) { throw 'Faltan los terminos del instalador' }

Write-Host 'OK: comprobaciones de Release superadas.' -ForegroundColor Green
