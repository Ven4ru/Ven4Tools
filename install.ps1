# Ven4Tools -- installer script
# Usage: irm ven4tools.ru/install.ps1 | iex
#   alt: irm raw.githubusercontent.com/Ven4ru/Ven4Tools/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "  Ven4Tools  |  ven4tools.ru" -ForegroundColor Cyan
Write-Host ""

try {
    Write-Host "  Fetching latest version..." -ForegroundColor Gray
    $api = Invoke-RestMethod 'https://ven4tools.ru/api/latest_version.php' -UseBasicParsing
    $url = $api.downloads.launcher
    $ver = $api.version

    if (-not $url) { throw 'Failed to get installer URL' }

    Write-Host "  Version: $ver" -ForegroundColor White
    Write-Host "  Downloading..." -ForegroundColor Gray

    $tmp = Join-Path $env:TEMP 'Ven4Tools.Setup.exe'
    Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing

    # Проверка целостности, если сервер отдаёт хеш. Пока launcher_sha256 не
    # реализован в latest_version.php — блок условный и не ломает установку;
    # включится автоматически, как только сервер начнёт отдавать хеш.
    if ($api.downloads.launcher_sha256) {
        Write-Host "  Verifying..." -ForegroundColor Gray
        $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
        if ($actual -ne $api.downloads.launcher_sha256.ToUpper()) {
            Remove-Item $tmp -Force
            throw "SHA256 mismatch — installation aborted"
        }
    }

    Write-Host "  Installing..." -ForegroundColor Gray
    $proc = Start-Process $tmp -ArgumentList '/S' -PassThru -Wait
    if (Test-Path $tmp) { Remove-Item $tmp -Force }

    Write-Host ""
    Write-Host "  Done! Ven4Tools installed." -ForegroundColor Green
    Write-Host "  Launch from Start Menu or Desktop shortcut." -ForegroundColor DarkGray
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "  Error: $_" -ForegroundColor Red
    Write-Host ""
    exit 1
}
