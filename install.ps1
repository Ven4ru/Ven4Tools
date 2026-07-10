# Ven4Tools -- installer script
# Usage: irm ven4tools.ru/install.ps1 | iex
#   alt: irm raw.githubusercontent.com/Ven4ru/Ven4Tools/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "  Ven4Tools  |  ven4tools.ru" -ForegroundColor Cyan
Write-Host ""

try {
    Write-Host "  Получение последней версии..." -ForegroundColor Gray
    $api = Invoke-RestMethod 'https://ven4tools.ru/api/latest_version.php' -UseBasicParsing
    $url = $api.downloads.launcher
    $ver = $api.version

    if (-not $url) { throw 'Не удалось получить ссылку на установщик' }

    Write-Host "  Версия: $ver" -ForegroundColor White
    Write-Host "  Загрузка..." -ForegroundColor Gray

    $tmp = Join-Path $env:TEMP 'Ven4Tools.Setup.exe'
    Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing

    # Проверка целостности, если сервер отдаёт хеш. Блок условный — не ломает
    # установку, если сервер вдруг перестанет отдавать хеш.
    if ($api.downloads.launcher_sha256) {
        Write-Host "  Проверка целостности..." -ForegroundColor Gray
        $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
        if ($actual -ne $api.downloads.launcher_sha256.ToUpper()) {
            Remove-Item $tmp -Force
            throw "Несовпадение SHA256 — установка прервана"
        }
    }

    Write-Host "  Установка..." -ForegroundColor Gray
    $proc = Start-Process $tmp -ArgumentList '/S' -PassThru -Wait
    if (Test-Path $tmp) { Remove-Item $tmp -Force }

    if ($proc.ExitCode -ne 0) {
        throw "Установщик завершился с кодом $($proc.ExitCode)"
    }

    Write-Host ""
    Write-Host "  Готово! Ven4Tools установлен." -ForegroundColor Green
    Write-Host "  Запустите из меню «Пуск» или с ярлыка на рабочем столе." -ForegroundColor DarkGray
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "  Ошибка: $_" -ForegroundColor Red
    Write-Host ""
    exit 1
}
