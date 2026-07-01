param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Ven4Tools\Launcher'
$launcherPath = Join-Path $installDir 'Ven4Tools.Launcher.exe'
$uninstallPath = Join-Path $installDir 'uninstall.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Ven4Tools'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Ven4Tools Launcher.lnk'
$startShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Ven4Tools\Ven4Tools Launcher.lnk'

foreach ($requiredPath in @($launcherPath, $uninstallPath, $desktopShortcut, $startShortcut)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Не найден обязательный файл установки: $requiredPath"
    }
}

if (-not (Test-Path $uninstallKey)) {
    throw 'Не найдена запись лаунчера в «Программы и компоненты».'
}

$fileVersion = (Get-Item -LiteralPath $launcherPath).VersionInfo.FileVersion
$registry = Get-ItemProperty $uninstallKey

if (-not $fileVersion.StartsWith($ExpectedVersion, [StringComparison]::Ordinal)) {
    throw "Версия launcher exe $fileVersion не совпадает с ожидаемой $ExpectedVersion."
}
if ($registry.DisplayVersion -ne $ExpectedVersion) {
    throw "Версия в реестре $($registry.DisplayVersion) не совпадает с ожидаемой $ExpectedVersion."
}
if ([System.IO.Path]::GetFullPath($registry.InstallLocation) -ne [System.IO.Path]::GetFullPath($installDir)) {
    throw "Некорректный каталог установки: $($registry.InstallLocation)"
}

Write-Host "Launcher $ExpectedVersion установлен и зарегистрирован корректно." -ForegroundColor Green
