<#
.SYNOPSIS
Подписывает version.json ECDSA-ключом (Ven4Tools.UpdateManifest.v1) и
атомарно заливает его вместе с .sig на CDN.

.DESCRIPTION
version.json — единственный источник URL и SHA256 обновлений клиента и
лаунчера. Без подписи компрометация CDN означала бы одновременную подмену
обоих контролей целостности (HIGH-находка аудита безопасности 2026-07-13).
Подпись создаётся офлайн этим скриптом — приватный ключ никогда не
покидает эту машину и не оказывается на CDN.

.PARAMETER VersionJsonPath
Путь к локальному version.json с уже обновлённым содержимым (версия,
zip_url/zip_sha256 клиента, setup_url/setup_sha256 лаунчера).

.EXAMPLE
.\Tools\deploy-version-manifest.ps1 -VersionJsonPath .\version.json
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$VersionJsonPath,

    [string]$PrivateKeyPath = "$env:USERPROFILE\.ven4tools\update-manifest-signing-private.pem",
    [string]$PublicKeyPath = "$env:USERPROFILE\.ven4tools\update-manifest-signing-public.pem",
    [string]$SignerDll = "$PSScriptRoot\UpdateManifestSigner\bin\Release\net8.0\UpdateManifestSigner.dll",

    # Разрешить публикацию манифеста, в котором версия клиента или лаунчера СТАРШЕ
    # уже опубликованной на CDN. Нужен только для осознанного отката релиза.
    [switch]$AllowClientDowngrade
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $VersionJsonPath)) { throw "Не найден $VersionJsonPath" }
if (-not (Test-Path $PrivateKeyPath)) {
    throw "Не найден приватный ключ подписи манифеста: $PrivateKeyPath. " +
          "Ключ не хранится в репозитории — он должен быть на этой машине отдельно."
}
if (-not (Test-Path $SignerDll)) {
    Write-Host "UpdateManifestSigner не собран — собираю..."
    dotnet build "$PSScriptRoot\UpdateManifestSigner\UpdateManifestSigner.csproj" -c Release --nologo | Out-Null
}

# ----------------------------------------------------------------------------
# Защита от отката манифеста
# ----------------------------------------------------------------------------
# version.json лежит в .gitignore: он не версионируется, никем не синхронизируется
# после деплоя и потому спокойно отстаёт от того, что реально опубликовано. Живой
# случай 2026-09-10: локальная копия несла клиент 5.1.0, тогда как на CDN уже был
# 5.1.1 вместе с полями files_base_mirror_hosting/updated_at и расширенным
# historicalClientArchives — деплой лаунчерного релиза из неё откатил бы всю
# клиентскую часть манифеста, и клиенты 5.1.1 остались бы без манифеста своей версии.
# Поймали руками, но полагаться на внимательность здесь нельзя.
if (-not $AllowClientDowngrade) {
    try {
        $liveRaw = (Invoke-WebRequest "https://cdn.ven4tools.ru/version.json" -UseBasicParsing -TimeoutSec 20).Content
        $live = $liveRaw | ConvertFrom-Json
        $local = Get-Content $VersionJsonPath -Raw | ConvertFrom-Json

        foreach ($part in @(
            @{ Name = "клиента";  Live = $live.client.version;   Local = $local.client.version },
            @{ Name = "лаунчера"; Live = $live.launcher.version; Local = $local.launcher.version }
        )) {
            if (-not $part.Live -or -not $part.Local) { continue }
            $liveVer = [version]$part.Live
            $localVer = [version]$part.Local
            if ($localVer -lt $liveVer) {
                throw "Отказ: версия $($part.Name) в локальном манифесте ($($part.Local)) СТАРШЕ той, " +
                      "что уже опубликована на CDN ($($part.Live)). Локальный version.json отстал — " +
                      "возьмите за основу живой манифест (https://cdn.ven4tools.ru/version.json) и " +
                      "правьте в нём только нужный блок. Осознанный откат — ключ -AllowClientDowngrade."
            }
        }
        Write-Host "Проверка отката: ок (CDN client=$($live.client.version) launcher=$($live.launcher.version))"
    }
    catch [System.Net.WebException] {
        # CDN недоступен — сверять не с чем. Это не повод блокировать деплой:
        # именно ради недоступного CDN манифест и переливают.
        Write-Warning "CDN недоступен, проверка отката пропущена: $($_.Exception.Message)"
    }
}

$sigPath = "$VersionJsonPath.sig"
Remove-Item $sigPath -ErrorAction SilentlyContinue

Write-Host "Подписываю $VersionJsonPath..."
dotnet $SignerDll $VersionJsonPath $PrivateKeyPath
if (-not (Test-Path $sigPath)) { throw "Подпись не создана — проверь вывод UpdateManifestSigner выше." }

# Самопроверка локально созданной пары до заливки на CDN — ловит баги самого
# signer'а или неверный ключ ещё до того, как что-либо уйдёт в прод.
Write-Host "Проверяю подпись локально..."
dotnet $SignerDll verify $VersionJsonPath $sigPath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "Локальная подпись не прошла проверку — заливка на CDN отменена." }

Write-Host "Заливаю на CDN (jump:/var/www/cdn/)..."
scp $VersionJsonPath "jump:/tmp/version.json.new"
scp $sigPath "jump:/tmp/version.json.sig.new"
# mv на удалённой стороне — атомарная замена обоих файлов разом, без окна
# "manifest уже новый, подпись ещё старая" (или наоборот). Одна строка —
# backtick-перенос внутри двойных кавычек здесь ломался (экранировался как
# литеральный символ, а не перенос строки), remote bash получал буквальные
# backtick'и и падал на command substitution.
$remoteCmd = "mv /tmp/version.json.new /var/www/cdn/version.json && mv /tmp/version.json.sig.new /var/www/cdn/version.json.sig && chown root:root /var/www/cdn/version.json /var/www/cdn/version.json.sig && chmod 644 /var/www/cdn/version.json /var/www/cdn/version.json.sig"
ssh jump $remoteCmd

Write-Host "Проверка публичной доступности..."
# -UseBasicParsing: без него Invoke-WebRequest в Windows PowerShell 5.1
# пытается использовать IE DOM-парсер, что в неинтерактивном режиме
# (CI, автоматизация) падает с "NonInteractive mode" вместо реального запроса.
#
# Сверяем то, что реально отдаёт CDN (а не локальные файлы) — единственный
# способ поймать порчу байтов при заливке/на стороне CDN. Качаем -OutFile
# напрямую в бинарном виде: Invoke-WebRequest .Content — это .NET string,
# и любой последующий Set-Content/[IO.File]::WriteAllText неизбежно
# перекодирует её (в Windows PowerShell 5.1 -Encoding utf8 добавляет BOM),
# что ломает побайтовое сравнение подписи независимо от того, реально ли
# CDN отдал корректную пару.
$remoteCheckDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ven4tools-manifest-check-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $remoteCheckDir | Out-Null
try {
    $remoteJsonFile = Join-Path $remoteCheckDir "version.json"
    $remoteSigFile = Join-Path $remoteCheckDir "version.json.sig"
    Invoke-WebRequest "https://cdn.ven4tools.ru/version.json" -OutFile $remoteJsonFile -UseBasicParsing
    $remoteSigResp = Invoke-WebRequest "https://cdn.ven4tools.ru/version.json.sig" -OutFile $remoteSigFile -UseBasicParsing -PassThru

    dotnet $SignerDll verify $remoteJsonFile $remoteSigFile $PublicKeyPath
    if ($LASTEXITCODE -ne 0) {
        throw "КРИТИЧНО: подпись на CDN не соответствует залитому version.json — пользователи получат отказ в установке. Проверь /var/www/cdn/ на jump-хосте немедленно."
    }

    $remoteJson = Get-Content $remoteJsonFile -Raw | ConvertFrom-Json
    Write-Host "OK: client=$($remoteJson.client.version) launcher=$($remoteJson.launcher.version), sig HTTP $($remoteSigResp.StatusCode), подпись подтверждена по данным с CDN"
}
finally {
    Remove-Item $remoteCheckDir -Recurse -Force -ErrorAction SilentlyContinue
}
