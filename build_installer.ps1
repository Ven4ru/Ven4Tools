# ============================================================================
# build_installer.ps1 — сборка установщика Ven4Tools Launcher
# ============================================================================
# Использование (из корня репозитория):
#   .\build_installer.ps1 -Version "2.1.0"
#
# Делает:
#   1. dotnet publish лаунчера (Release, win-x64, self-contained, single-file)
#      с прошивкой версии через -p:Version (csproj не редактируется);
#   2. Копирует чистый exe в _release\Ven4Tools.Launcher-X.Y.Z.exe
#      (имя по правилам релизов проекта);
#   3. Компилирует installer\Ven4Tools.Setup.nsi через makensis
#      → _release\Ven4Tools.Setup-X.Y.Z.exe.
#
# Требования: .NET 8 SDK, NSIS 3.x (winget install NSIS.NSIS).
# Совместим с Windows PowerShell 5.1 и PowerShell 7.
# ============================================================================

param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host ""
Write-Host "=== Сборка установщика Ven4Tools Launcher $Version ===" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# 0. Проверка окружения
# ----------------------------------------------------------------------------
$csproj = Join-Path $root "Ven4Tools.Launcher\Ven4Tools.Launcher.csproj"
$nsiScript = Join-Path $root "installer\Ven4Tools.Setup.nsi"

if (-not (Test-Path $csproj)) {
    throw "Не найден проект лаунчера: $csproj. Запускайте скрипт из корня репозитория."
}
if (-not (Test-Path $nsiScript)) {
    throw "Не найден NSIS-скрипт: $nsiScript"
}

# Поиск makensis: сначала PATH, затем стандартные папки установки NSIS
$makensis = $null
$cmd = Get-Command makensis -ErrorAction SilentlyContinue
if ($null -ne $cmd) {
    $makensis = $cmd.Source
} else {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe"),
        (Join-Path $env:ProgramFiles "NSIS\makensis.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\NSIS\makensis.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { $makensis = $candidate; break }
    }
}
if ($null -eq $makensis) {
    throw "makensis.exe не найден. Установите NSIS: winget install NSIS.NSIS (или https://nsis.sourceforge.io)"
}
Write-Host "makensis: $makensis"

# ----------------------------------------------------------------------------
# 1. Публикация лаунчера
# ----------------------------------------------------------------------------
Write-Host ""
Write-Host "[1/3] dotnet publish (Release, win-x64, self-contained)..." -ForegroundColor Yellow

dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    "-p:Version=$Version" `
    "-p:AssemblyVersion=$Version" `
    "-p:FileVersion=$Version"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с ошибкой (код $LASTEXITCODE)."
}

$publishDir = Join-Path $root "Ven4Tools.Launcher\bin\Release\net8.0-windows\win-x64\publish"
$publishedExe = Join-Path $publishDir "Ven4Tools.Launcher.exe"
if (-not (Test-Path $publishedExe)) {
    throw "После publish не найден exe: $publishedExe"
}

# Контроль: версия в exe должна совпадать с запрошенной
$fileVersion = (Get-Item $publishedExe).VersionInfo.FileVersion
Write-Host "Опубликован exe, FileVersion = $fileVersion"
if (-not $fileVersion.StartsWith($Version)) {
    throw "Версия exe ($fileVersion) не совпадает с запрошенной ($Version)."
}

# ----------------------------------------------------------------------------
# 2. Копия чистого exe в _release (стандартный релизный ассет)
# ----------------------------------------------------------------------------
Write-Host ""
Write-Host "[2/3] Копирование exe в _release..." -ForegroundColor Yellow

$releaseDir = Join-Path $root "_release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$launcherAsset = Join-Path $releaseDir "Ven4Tools.Launcher-$Version.exe"
Copy-Item $publishedExe $launcherAsset -Force
Write-Host "-> $launcherAsset"

# ----------------------------------------------------------------------------
# 3. Компиляция установщика NSIS
# ----------------------------------------------------------------------------
Write-Host ""
Write-Host "[3/3] makensis..." -ForegroundColor Yellow

$setupAsset = Join-Path $releaseDir "Ven4Tools.Setup-$Version.exe"

# /INPUTCHARSET UTF8 — в .nsi русские строки в кодировке UTF-8
& $makensis `
    "/INPUTCHARSET" "UTF8" `
    "/DVERSION=$Version" `
    "/DPUBLISH_DIR=$publishDir" `
    "/DOUTFILE=$setupAsset" `
    $nsiScript

if ($LASTEXITCODE -ne 0) {
    throw "makensis завершился с ошибкой (код $LASTEXITCODE)."
}
if (-not (Test-Path $setupAsset)) {
    throw "Установщик не создан: $setupAsset"
}

# ----------------------------------------------------------------------------
# Итог
# ----------------------------------------------------------------------------
$launcherSizeMb = [math]::Round((Get-Item $launcherAsset).Length / 1MB, 1)
$setupSizeMb    = [math]::Round((Get-Item $setupAsset).Length / 1MB, 1)

Write-Host ""
Write-Host "=== Готово ===" -ForegroundColor Green
Write-Host "Лаунчер:    $launcherAsset ($launcherSizeMb МБ)"
Write-Host "Установщик: $setupAsset ($setupSizeMb МБ)"
Write-Host ""
Write-Host "Проверка установки после запуска установщика:"
Write-Host ".\tests\scripts\verify_launcher_install.ps1 -ExpectedVersion $Version"
