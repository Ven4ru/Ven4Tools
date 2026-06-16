# ============================================================================
# test_install.ps1 — проверка корректности установки Ven4Tools Launcher
# ============================================================================
# Использование (после запуска установщика):
#   .\test_install.ps1                          — все проверки
#   .\test_install.ps1 -ExpectedVersion 2.0.0   — плюс сверка версии в реестре
#
# Печатает [PASS]/[FAIL] для каждой проверки и итог.
# Код возврата: 0 — все проверки пройдены, 1 — есть провалы.
# Совместим с Windows PowerShell 5.1 и PowerShell 7.
# ============================================================================

param(
    [string]$ExpectedVersion = ""
)

$ErrorActionPreference = 'SilentlyContinue'

# --- Ожидаемые пути и значения (должны совпадать с Ven4Tools.Setup.nsi) ---
$installDir   = Join-Path $env:LOCALAPPDATA "Ven4Tools\Launcher"
$exePath      = Join-Path $installDir "Ven4Tools.Launcher.exe"
$uninstPath   = Join-Path $installDir "uninstall.exe"
$regPath      = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Ven4Tools"
$desktopLnk   = Join-Path ([Environment]::GetFolderPath('Desktop')) "Ven4Tools Launcher.lnk"
$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) "Ven4Tools"
$startLnk     = Join-Path $startMenuDir "Ven4Tools Launcher.lnk"
$uninstLnk    = Join-Path $startMenuDir "Удалить Ven4Tools Launcher.lnk"

$script:passCount = 0
$script:failCount = 0

function Write-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Details = ""
    )
    if ($Passed) {
        Write-Host "[PASS] $Name" -ForegroundColor Green
        $script:passCount++
    } else {
        if ([string]::IsNullOrEmpty($Details)) {
            Write-Host "[FAIL] $Name" -ForegroundColor Red
        } else {
            Write-Host "[FAIL] $Name — $Details" -ForegroundColor Red
        }
        $script:failCount++
    }
}

Write-Host ""
Write-Host "=== Проверка установки Ven4Tools Launcher ===" -ForegroundColor Cyan
Write-Host ""

# ----------------------------------------------------------------------------
# 1. Файлы в папке установки
# ----------------------------------------------------------------------------
Write-Check "Файл лаунчера существует: $exePath" (Test-Path $exePath)
Write-Check "Деинсталлятор существует: $uninstPath" (Test-Path $uninstPath)

# ----------------------------------------------------------------------------
# 2. Реестр: запись в «Программы и компоненты»
# ----------------------------------------------------------------------------
$reg = Get-ItemProperty -Path $regPath
Write-Check "Ключ реестра Uninstall\Ven4Tools существует" ($null -ne $reg)

if ($null -ne $reg) {
    Write-Check "DisplayName = 'Ven4Tools Launcher'" `
        ($reg.DisplayName -eq "Ven4Tools Launcher") "фактически: '$($reg.DisplayName)'"

    Write-Check "Publisher = 'Ven4ru'" `
        ($reg.Publisher -eq "Ven4ru") "фактически: '$($reg.Publisher)'"

    if ([string]::IsNullOrEmpty($ExpectedVersion)) {
        Write-Check "DisplayVersion заполнен (формат X.Y.Z)" `
            ($reg.DisplayVersion -match '^\d+\.\d+\.\d+$') "фактически: '$($reg.DisplayVersion)'"
    } else {
        Write-Check "DisplayVersion = '$ExpectedVersion'" `
            ($reg.DisplayVersion -eq $ExpectedVersion) "фактически: '$($reg.DisplayVersion)'"
    }

    $regInstallLoc = ""
    if ($null -ne $reg.InstallLocation) { $regInstallLoc = $reg.InstallLocation.TrimEnd('\') }
    Write-Check "InstallLocation указывает на папку установки" `
        ($regInstallLoc -eq $installDir.TrimEnd('\')) "фактически: '$($reg.InstallLocation)'"

    # UninstallString вида "C:\...\uninstall.exe" — убираем кавычки и проверяем файл
    $uninstFromReg = ""
    if ($null -ne $reg.UninstallString) { $uninstFromReg = $reg.UninstallString.Trim('"') }
    Write-Check "UninstallString указывает на существующий uninstall.exe" `
        ((-not [string]::IsNullOrEmpty($uninstFromReg)) -and (Test-Path $uninstFromReg)) `
        "фактически: '$($reg.UninstallString)'"

    Write-Check "EstimatedSize больше нуля" `
        ($null -ne $reg.EstimatedSize -and $reg.EstimatedSize -gt 0) "фактически: '$($reg.EstimatedSize)'"

    Write-Check "NoModify = 1" ($reg.NoModify -eq 1) "фактически: '$($reg.NoModify)'"
    Write-Check "NoRepair = 1" ($reg.NoRepair -eq 1) "фактически: '$($reg.NoRepair)'"
} else {
    Write-Host "       (проверки значений реестра пропущены — ключ отсутствует)" -ForegroundColor DarkGray
    $script:failCount += 8
}

# ----------------------------------------------------------------------------
# 3. Ярлыки (существование + цель)
# ----------------------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell

Write-Check "Ярлык на рабочем столе существует" (Test-Path $desktopLnk)
if (Test-Path $desktopLnk) {
    $target = $shell.CreateShortcut($desktopLnk).TargetPath
    Write-Check "Ярлык на рабочем столе ведёт на exe лаунчера" `
        ($target -eq $exePath) "фактически: '$target'"
}

Write-Check "Ярлык в меню «Пуск» существует" (Test-Path $startLnk)
if (Test-Path $startLnk) {
    $target = $shell.CreateShortcut($startLnk).TargetPath
    Write-Check "Ярлык в меню «Пуск» ведёт на exe лаунчера" `
        ($target -eq $exePath) "фактически: '$target'"
}

Write-Check "Ярлык деинсталлятора в меню «Пуск» существует" (Test-Path $uninstLnk)

[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

# ----------------------------------------------------------------------------
# 4. Версия файла exe (если задана ожидаемая)
# ----------------------------------------------------------------------------
if ((-not [string]::IsNullOrEmpty($ExpectedVersion)) -and (Test-Path $exePath)) {
    $fileVersion = (Get-Item $exePath).VersionInfo.FileVersion
    $versionOk = $false
    if ($null -ne $fileVersion) { $versionOk = $fileVersion.StartsWith($ExpectedVersion) }
    Write-Check "FileVersion exe начинается с '$ExpectedVersion'" $versionOk "фактически: '$fileVersion'"
}

# ----------------------------------------------------------------------------
# Итог
# ----------------------------------------------------------------------------
Write-Host ""
if ($script:failCount -eq 0) {
    Write-Host "=== РЕЗУЛЬТАТ: все проверки пройдены ($script:passCount/$($script:passCount)) ===" -ForegroundColor Green
    exit 0
} else {
    $total = $script:passCount + $script:failCount
    Write-Host "=== РЕЗУЛЬТАТ: провалено $script:failCount из $total проверок ===" -ForegroundColor Red
    exit 1
}
