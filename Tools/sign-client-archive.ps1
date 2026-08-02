<#
.SYNOPSIS
Подписывает уже собранный zip-архив клиента ECDSA-ключом (Ven4Tools.ClientArchive.v1)
для последующей офлайн-установки из локального файла в лаунчере.

.DESCRIPTION
Запускать ПОСЛЕ обычной сборки zip (dotnet publish + Compress-Archive), ДО подсчёта
whole-file SHA256 для version.json (см. deploy-version-manifest.ps1) — подпись
дописывается внутрь архива как новая запись, поэтому исходный файл после этого шага
на одну запись длиннее, и whole-file SHA256 нужно считать уже над этим, финальным
файлом.

.EXAMPLE
.\Tools\sign-client-archive.ps1 -ArchivePath .\_release\Ven4Tools-Client-4.4.3.zip -Version 4.4.3
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PrivateKeyPath = "$env:USERPROFILE\.ven4tools\client-archive-signing-private.pem",
    [string]$PublicKeyPath = "$env:USERPROFILE\.ven4tools\client-archive-signing-public.pem",
    [string]$SignerDll = "$PSScriptRoot\ClientArchiveSigner\bin\Release\net8.0\ClientArchiveSigner.dll"
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1: $PSScriptRoot в значении параметра по умолчанию не
# резолвится, если в том же param()-блоке есть Mandatory-параметры (известная
# особенность биндера, в PowerShell 7 отсутствует) — поэтому досчитываем путь
# здесь, если -SignerDll не был передан явно.
if (-not $PSBoundParameters.ContainsKey('SignerDll')) {
    $SignerDll = Join-Path $PSScriptRoot "ClientArchiveSigner\bin\Release\net8.0\ClientArchiveSigner.dll"
}

if (-not (Test-Path $ArchivePath)) { throw "Не найден $ArchivePath" }
if (-not (Test-Path $PrivateKeyPath)) {
    throw "Не найден приватный ключ подписи архива клиента: $PrivateKeyPath. " +
          "Ключ не хранится в репозитории — он должен быть на этой машине отдельно."
}
if (-not (Test-Path $SignerDll)) {
    Write-Host "ClientArchiveSigner не собран — собираю..."
    dotnet build "$PSScriptRoot\ClientArchiveSigner\ClientArchiveSigner.csproj" -c Release --nologo | Out-Null
}

Write-Host "Подписываю $ArchivePath (версия $Version)..."
dotnet $SignerDll $ArchivePath $PrivateKeyPath $Version
if ($LASTEXITCODE -ne 0) { throw "Подпись не создана — проверь вывод ClientArchiveSigner выше." }

Write-Host "Проверяю подпись локально..."
dotnet $SignerDll verify $ArchivePath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "Локальная подпись не прошла проверку." }

Write-Host "Готово. Архив подписан — теперь считайте whole-file SHA256 для version.json над ЭТИМ файлом."
