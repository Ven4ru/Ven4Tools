<#
.SYNOPSIS
    Выкладывает архив клиента и/или установщик лаунчера на CDN и обновляет version.json.

.DESCRIPTION
    Раньше выкладка шла руками: scp файла, правка version.json в редакторе, перенос
    прежней версии клиента в historicalClientArchives, затем deploy-version-manifest.ps1.
    Для лаунчера скрипта не было вовсе. Каждая ручная правка манифеста — шанс потерять
    поле или оставить старый хеш, а ошибка в version.json ломает обновление у всех.

    Скрипт делает то же самое одной командой:
      1. Берёт за основу ЖИВОЙ version.json с CDN (локальная копия не версионируется
         и легко отстаёт — см. защиту от отката в deploy-version-manifest.ps1).
      2. Атомарно заливает переданные файлы в /var/www/cdn/releases (.tmp + mv)
         и сверяет SHA256 на сервере с локальным.
      3. Меняет в манифесте только блоки выкладываемых компонентов. Для клиента
         прежняя пара version/sha256 переносится в historicalClientArchives.
      4. Подписывает и выкладывает манифест через deploy-version-manifest.ps1
         (там же — публичная проверка подписи).
      5. Проверяет публичную доступность файлов по адресам из манифеста.

    Файлы дельта-обновления клиента (client-files/<версия>/) этот скрипт не
    выкладывает — сначала Tools\deploy-client-manifest.ps1. Если манифеста файлов
    новой версии на CDN нет, выкладка клиента останавливается: иначе лаунчер
    получил бы ссылку на несуществующий манифест.

.PARAMETER ClientZip
    Подписанный архив клиента Ven4Tools-Client-X.Y.Z.zip (после sign-client-archive.ps1).

.PARAMETER LauncherSetup
    Установщик лаунчера Ven4Tools.Setup-X.Y.Z.exe.

.PARAMETER VersionJsonPath
    Куда сохранить собранный манифест перед подписью. По умолчанию — version.json
    в корне репозитория (в .gitignore).

.PARAMETER SkipClientFilesCheck
    Не требовать манифест файлов клиента на CDN. Поля manifest_url и files_base_*
    тогда из блока клиента убираются, и лаунчер обновляет клиента полным архивом.

.EXAMPLE
    .\Tools\deploy-cdn-release.ps1 -ClientZip .\_release\Ven4Tools-Client-5.4.0.zip

.EXAMPLE
    .\Tools\deploy-cdn-release.ps1 -LauncherSetup .\_release\Ven4Tools.Setup-3.9.0.exe

.NOTES
    Требуется SSH-алиас "jump" (тот же, что у deploy-version-manifest.ps1).
#>
param(
    [string]$ClientZip,
    [string]$LauncherSetup,
    [string]$VersionJsonPath = (Join-Path $PSScriptRoot '..\version.json'),
    [switch]$SkipClientFilesCheck
)

$ErrorActionPreference = 'Stop'

$CdnBase = 'https://cdn.ven4tools.ru'
$MirrorBase = 'https://ven4tools.ru'
$GitHubBase = 'https://github.com/Ven4ru/Ven4Tools/releases/download'
$RemoteReleases = '/var/www/cdn/releases'

if (-not $ClientZip -and -not $LauncherSetup) {
    throw 'Нечего выкладывать: укажите -ClientZip и/или -LauncherSetup.'
}

function Get-ArtifactVersion {
    param([string]$Path, [string]$Pattern, [string]$Kind)

    if (-not (Test-Path -LiteralPath $Path)) { throw "Файл не найден: $Path" }
    $name = Split-Path -Leaf $Path
    $match = [regex]::Match($name, $Pattern)
    if (-not $match.Success) { throw "Имя файла $Kind не по шаблону: $name" }
    return $match.Groups['version'].Value
}

function Publish-Artifact {
    <# Атомарная заливка и сверка хеша на сервере. Возвращает SHA256 в нижнем регистре. #>
    param([string]$Path)

    $name = Split-Path -Leaf $Path
    $sha = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Заливка $name (sha256 $sha)..."

    # $ErrorActionPreference=Stop не распространяется на нативные exe — код возврата
    # проверяется явно после каждого вызова.
    scp $Path "jump:$RemoteReleases/$name.tmp"
    if ($LASTEXITCODE -ne 0) { throw "Заливка $name не удалась — scp завершился с кодом $LASTEXITCODE." }

    $remote = ssh jump "sha256sum '$RemoteReleases/$name.tmp' | cut -d' ' -f1"
    if ($LASTEXITCODE -ne 0) { throw "Не удалось посчитать хеш $name на сервере — ssh завершился с кодом $LASTEXITCODE." }
    if (([string]$remote).Trim() -ne $sha) {
        ssh jump "rm -f '$RemoteReleases/$name.tmp'" | Out-Null
        throw "SHA256 $name на сервере не совпал с локальным: $remote против $sha. Временный файл удалён."
    }

    ssh jump "chmod 644 '$RemoteReleases/$name.tmp' && mv '$RemoteReleases/$name.tmp' '$RemoteReleases/$name'"
    if ($LASTEXITCODE -ne 0) { throw "Не удалось переименовать $name на сервере — ssh завершился с кодом $LASTEXITCODE." }
    return $sha
}

function Test-PublicUrl {
    param([string]$Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -TimeoutSec 30
        return $response.StatusCode -eq 200
    }
    catch { return $false }
}

function Set-Field {
    <# Добавляет или заменяет свойство, сохраняя порядок остальных. #>
    param($Object, [string]$Name, $Value)
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

# --- Разбор входных файлов ----------------------------------------------------

$clientVersion = $null
$launcherVersion = $null
if ($ClientZip) {
    $clientVersion = Get-ArtifactVersion -Path $ClientZip -Kind 'клиента' `
        -Pattern '^Ven4Tools-Client-(?<version>\d+\.\d+\.\d+)\.zip$'
}
if ($LauncherSetup) {
    $launcherVersion = Get-ArtifactVersion -Path $LauncherSetup -Kind 'лаунчера' `
        -Pattern '^Ven4Tools\.Setup-(?<version>\d+\.\d+\.\d+)\.exe$'
}

# --- Живой манифест как основа ------------------------------------------------

Write-Host "Чтение живого манифеста $CdnBase/version.json..."
$liveRaw = (Invoke-WebRequest "$CdnBase/version.json" -UseBasicParsing -TimeoutSec 30).Content
$manifest = $liveRaw | ConvertFrom-Json
if (-not $manifest.client -or -not $manifest.launcher) {
    throw 'Живой version.json не содержит блоков client/launcher — править такой манифест автоматически нельзя.'
}

# --- Клиент -------------------------------------------------------------------

if ($ClientZip) {
    $zipName = Split-Path -Leaf $ClientZip
    $previousVersion = [string]$manifest.client.version
    $previousSha = [string]$manifest.client.zip_sha256

    if ($previousVersion -and ([version]$clientVersion -lt [version]$previousVersion)) {
        throw "На CDN уже клиент $previousVersion — выкладка более старого $clientVersion отменена."
    }

    $manifestUrl = "$CdnBase/client-files/$clientVersion/client-manifest.json"
    $hasClientFiles = $false
    if (-not $SkipClientFilesCheck) {
        $hasClientFiles = (Test-PublicUrl $manifestUrl) -and (Test-PublicUrl "$manifestUrl.sig")
        if (-not $hasClientFiles) {
            throw "Манифест файлов клиента $clientVersion на CDN не найден ($manifestUrl). " +
                  'Сначала Tools\deploy-client-manifest.ps1, либо ключ -SkipClientFilesCheck (обновление полным архивом).'
        }
    }

    $sha = Publish-Artifact -Path $ClientZip

    # Прежняя версия — в список архивов, которые лаунчер принимает из локального файла.
    if ($previousVersion -and $previousSha -and $previousVersion -ne $clientVersion) {
        $history = @($manifest.historicalClientArchives | Where-Object { $_ -and $_.version -ne $previousVersion })
        $entry = [pscustomobject][ordered]@{ version = $previousVersion; sha256 = $previousSha }
        Set-Field $manifest 'historicalClientArchives' (@($entry) + $history)
    }

    Set-Field $manifest.client 'version' $clientVersion
    Set-Field $manifest.client 'zip_url' "$CdnBase/releases/$zipName"
    Set-Field $manifest.client 'zip_fallback' "$GitHubBase/v$clientVersion/$zipName"
    Set-Field $manifest.client 'zip_mirror_hosting' "$MirrorBase/releases/$zipName"
    Set-Field $manifest.client 'zip_sha256' $sha

    $deltaFields = [ordered]@{
        manifest_url              = $manifestUrl
        manifest_signature_url    = "$manifestUrl.sig"
        files_base_url            = "$CdnBase/client-files/$clientVersion/"
        files_base_mirror_hosting = "$MirrorBase/releases/client-files/$clientVersion/"
    }
    foreach ($field in $deltaFields.Keys) {
        if ($hasClientFiles) { Set-Field $manifest.client $field $deltaFields[$field] }
        elseif ($manifest.client.PSObject.Properties[$field]) { $manifest.client.PSObject.Properties.Remove($field) }
    }
}

# --- Лаунчер ------------------------------------------------------------------

if ($LauncherSetup) {
    $setupName = Split-Path -Leaf $LauncherSetup
    $previousLauncher = [string]$manifest.launcher.version
    if ($previousLauncher -and ([version]$launcherVersion -lt [version]$previousLauncher)) {
        throw "На CDN уже лаунчер $previousLauncher — выкладка более старого $launcherVersion отменена."
    }

    $sha = Publish-Artifact -Path $LauncherSetup

    Set-Field $manifest.launcher 'version' $launcherVersion
    Set-Field $manifest.launcher 'setup_url' "$CdnBase/releases/$setupName"
    Set-Field $manifest.launcher 'setup_fallback' "$GitHubBase/launcher-v$launcherVersion/$setupName"
    Set-Field $manifest.launcher 'setup_mirror_hosting' "$MirrorBase/releases/$setupName"
    Set-Field $manifest.launcher 'setup_sha256' $sha
}

Set-Field $manifest 'updated_at' ((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))

# --- Запись, подпись и выкладка манифеста -------------------------------------

$json = $manifest | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText(
    [System.IO.Path]::GetFullPath($VersionJsonPath), $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Манифест собран: $VersionJsonPath"

& (Join-Path $PSScriptRoot 'deploy-version-manifest.ps1') -VersionJsonPath $VersionJsonPath
if ($LASTEXITCODE -ne 0) { throw "deploy-version-manifest.ps1 завершился с кодом $LASTEXITCODE." }

# --- Публичная проверка -------------------------------------------------------

$urls = @()
if ($ClientZip) { $urls += $manifest.client.zip_url, $manifest.client.zip_mirror_hosting }
if ($LauncherSetup) { $urls += $manifest.launcher.setup_url, $manifest.launcher.setup_mirror_hosting }
$failed = @($urls | Where-Object { -not (Test-PublicUrl $_) })
if ($failed.Count -gt 0) {
    throw "Манифест выложен, но файлы недоступны публично: $($failed -join ', ')"
}

Write-Host ''
if ($ClientZip) { Write-Host "Клиент $clientVersion выложен: $($manifest.client.zip_url)" -ForegroundColor Green }
if ($LauncherSetup) { Write-Host "Лаунчер $launcherVersion выложен: $($manifest.launcher.setup_url)" -ForegroundColor Green }
Write-Host 'Запасной источник — GitHub: ассет релиза должен быть ТЕМ ЖЕ файлом (сверить SHA256 после gh release).' -ForegroundColor Yellow
