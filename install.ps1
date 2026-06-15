# Ven4Tools — установка лаунчера
# Использование: irm ven4tools.ru/install.ps1 | iex
#           или: irm raw.githubusercontent.com/Ven4ru/Ven4Tools/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "  Ven4Tools — установка" -ForegroundColor Cyan
Write-Host "  ven4tools.ru" -ForegroundColor DarkGray
Write-Host ""

try {
    Write-Host "  Получение актуальной версии..." -ForegroundColor Gray
    $api = Invoke-RestMethod 'https://ven4tools.ru/api/latest_version.php' -UseBasicParsing
    $url = $api.downloads.launcher
    $version = $api.version

    if (-not $url) { throw "Не удалось получить ссылку на установщик" }

    Write-Host "  Версия: $version" -ForegroundColor White
    Write-Host "  Загрузка установщика..." -ForegroundColor Gray

    $tmp = Join-Path $env:TEMP "Ven4Tools.Setup.exe"
    Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing

    Write-Host "  Установка..." -ForegroundColor Gray
    Start-Process $tmp '/S' -Wait
    Remove-Item $tmp -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "  Готово! Ven4Tools установлен." -ForegroundColor Green
    Write-Host "  Запустите лаунчер из меню Пуск или рабочего стола." -ForegroundColor DarkGray
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "  Ошибка установки: $_" -ForegroundColor Red
    Write-Host ""
    exit 1
}
