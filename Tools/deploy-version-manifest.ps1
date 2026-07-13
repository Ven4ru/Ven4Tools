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
    [string]$SignerDll = "$PSScriptRoot\UpdateManifestSigner\bin\Release\net8.0\UpdateManifestSigner.dll"
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

$sigPath = "$VersionJsonPath.sig"
Remove-Item $sigPath -ErrorAction SilentlyContinue

Write-Host "Подписываю $VersionJsonPath..."
dotnet $SignerDll $VersionJsonPath $PrivateKeyPath
if (-not (Test-Path $sigPath)) { throw "Подпись не создана — проверь вывод UpdateManifestSigner выше." }

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
$remoteJson = Invoke-RestMethod "https://cdn.ven4tools.ru/version.json"
# -UseBasicParsing: без него Invoke-WebRequest в Windows PowerShell 5.1
# пытается использовать IE DOM-парсер, что в неинтерактивном режиме
# (CI, автоматизация) падает с "NonInteractive mode" вместо реального запроса.
$remoteSigResp = Invoke-WebRequest "https://cdn.ven4tools.ru/version.json.sig" -UseBasicParsing
Write-Host "OK: client=$($remoteJson.client.version) launcher=$($remoteJson.launcher.version), sig HTTP $($remoteSigResp.StatusCode)"
