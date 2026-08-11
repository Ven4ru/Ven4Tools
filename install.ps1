# Ven4Tools -- installer script
#
# Usage: $t="$env:TEMP\v4t.ps1";irm ven4tools.ru/install.ps1 -OutFile $t;&$t
#   alt: irm raw.githubusercontent.com/Ven4ru/Ven4Tools/main/install.ps1 | iex
#
# ven4tools.ru отдаёт этот файл без charset в Content-Type (нет доступа к
# nginx-конфигу на шаред-хостинге, чтобы это исправить), из-за чего
# Invoke-RestMethod при прямом "irm | iex" декодирует UTF-8 как Latin-1 и
# кириллица в Write-Host превращается в кракозябры (сама установка при этом
# отрабатывает нормально — ломается только текст). Скачивание во временный
# файл с последующим запуском (`-OutFile` + `&`) заставляет PowerShell читать
# файл через его собственную BOM-детекцию вместо HTTP-декодера ответа — этот
# файл сохранён с UTF-8 BOM специально для этого. raw.githubusercontent.com
# отдаёт правильный charset сам, там "irm | iex" не ломается.

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "  Ven4Tools  |  ven4tools.ru" -ForegroundColor Cyan
Write-Host ""

try {
    # Версия уже установленного лаунчера (если есть) — берём с самого exe,
    # не из реестра: значение в реестре пишет тот же установщик и оно всегда
    # синхронно с exe, но чтение файла не зависит от того, успела ли прошлая
    # установка дойти до записи реестра.
    $installedExe = Join-Path $env:LOCALAPPDATA 'Ven4Tools\Launcher\Ven4Tools.Launcher.exe'
    $installedVersion = $null
    if (Test-Path $installedExe) {
        $installedVersion = (Get-Item $installedExe).VersionInfo.FileVersion
    }

    Write-Host "  Получение последней версии..." -ForegroundColor Gray
    $api = Invoke-RestMethod 'https://ven4tools.ru/api/latest_version.php' -UseBasicParsing
    $url = $api.downloads.launcher

    if (-not $url) { throw 'Не удалось получить ссылку на установщик' }

    # Ссылка приходит из ответа API, то есть из данных, а не из самого скрипта.
    # Дальше по ней скачивается и запускается EXE, поэтому источник проверяется по
    # тем же правилам, что и в лаунчере (Services/DownloadValidator.cs): подмена
    # ответа API не должна приводить к запуску файла с чужого сервера.
    #
    # Правила намеренно повторяют DownloadValidator.IsAllowedUri, включая
    # ограничение по пути для самого сайта: ven4tools.ru — обычный хост с API и
    # статикой, доверенным источником загрузки на нём является ТОЛЬКО /releases/.
    function Test-AllowedSource([uri]$candidate) {
        if ($candidate.Scheme -ne 'https') { return $false }
        $h = $candidate.Host.ToLowerInvariant()
        if ($h -eq 'ven4tools.ru' -or $h -eq 'www.ven4tools.ru') {
            return $candidate.AbsolutePath.StartsWith('/releases/', [StringComparison]::OrdinalIgnoreCase)
        }
        return (
            $h -eq 'github.com' -or $h.EndsWith('.github.com') -or
            $h -eq 'objects.githubusercontent.com' -or $h.EndsWith('.githubusercontent.com') -or
            $h -eq 'cdn.ven4tools.ru'
        )
    }

    $uri = [uri]$url
    if (-not (Test-AllowedSource $uri)) {
        throw "Недоверенный источник установщика: $($uri.Scheme)://$($uri.Host)$($uri.AbsolutePath) — установка прервана"
    }

    Write-Host "  Загрузка..." -ForegroundColor Gray
    $tmp = Join-Path $env:TEMP 'Ven4Tools.Setup.exe'
    $resp = Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing -PassThru

    # Проверка итогового адреса после редиректов: Invoke-WebRequest следует за ними
    # автоматически, поэтому проверки исходной ссылки мало — файл мог приехать с
    # другого хоста (штатный случай: github.com → *.githubusercontent.com).
    # Зеркалирует DownloadValidator.IsAllowedDownloadHostAfterRedirect в лаунчере.
    # Свойство с итоговым адресом называется по-разному в Windows PowerShell 5.1
    # и PowerShell 7+, поэтому берём то, которое реально есть.
    $finalUri = $null
    $base = $resp.BaseResponse
    if ($base -and $base.ResponseUri) { $finalUri = [uri]$base.ResponseUri }
    elseif ($base -and $base.RequestMessage -and $base.RequestMessage.RequestUri) { $finalUri = [uri]$base.RequestMessage.RequestUri }

    if ($finalUri -and -not (Test-AllowedSource $finalUri)) {
        if (Test-Path $tmp) { Remove-Item $tmp -Force }
        throw "Загрузка перенаправлена на недоверенный источник: $($finalUri.Scheme)://$($finalUri.Host)$($finalUri.AbsolutePath) — установка прервана"
    }

    # Проверка целостности — fail-closed, как и весь остальной проект
    # (DownloadValidator/InstallationService в клиенте и лаунчере). Раньше при
    # пустом хеше (latest_version.php оставляет launcher_sha256 пустым, если
    # сам не смог скачать и захешировать ассет с GitHub) скрипт всё равно
    # запускал EXE с жёлтым предупреждением — единственное fail-open место
    # во всём проекте. Кэш хеша на сервере живёт 24ч, так что это редкий
    # транзиентный сбой, а не штатный режим — не молча смиряемся с ним.
    if (-not $api.downloads.launcher_sha256) {
        throw "Сервер не отдал SHA256 установщика — установка прервана. Попробуйте ещё раз через несколько минут."
    }
    Write-Host "  Проверка целостности..." -ForegroundColor Gray
    $actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($actual -ne $api.downloads.launcher_sha256.ToUpper()) {
        Remove-Item $tmp -Force
        throw "Несовпадение SHA256 — установка прервана"
    }

    # Версия загруженного установщика — из его собственных метаданных exe
    # (сборка проставляет FileVersion = версии лаунчера, см.
    # Ven4Tools.Setup.nsi), не из $api.version — то поле относится к клиенту,
    # не к лаунчеру, который ставит этот скрипт.
    $newVersion = (Get-Item $tmp).VersionInfo.FileVersion

    if ($installedVersion -and ([version]$newVersion -le [version]$installedVersion)) {
        Write-Host "  Установлена актуальная версия: $installedVersion" -ForegroundColor White
        Write-Host "  Обновление не требуется." -ForegroundColor DarkGray
        Remove-Item $tmp -Force
        Write-Host ""
        exit 0
    }

    if ($installedVersion) {
        Write-Host "  Обновление: $installedVersion -> $newVersion" -ForegroundColor White
    } else {
        Write-Host "  Версия: $newVersion" -ForegroundColor White
    }

    Write-Host "  Установка..." -ForegroundColor Gray
    $proc = Start-Process $tmp -ArgumentList '/S' -PassThru -Wait
    if (Test-Path $tmp) { Remove-Item $tmp -Force }

    if ($proc.ExitCode -ne 0) {
        throw "Установщик завершился с кодом $($proc.ExitCode)"
    }

    Write-Host ""
    if ($installedVersion) {
        Write-Host "  Готово! Ven4Tools обновлён до версии $newVersion." -ForegroundColor Green
    } else {
        Write-Host "  Готово! Ven4Tools установлен." -ForegroundColor Green
    }
    Write-Host "  Запустите из меню «Пуск» или с ярлыка на рабочем столе." -ForegroundColor DarkGray
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "  Ошибка: $_" -ForegroundColor Red
    Write-Host ""
    exit 1
}
