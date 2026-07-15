<#
.SYNOPSIS
Подписывает Catalog/notifications.json ECDSA-ключом (Ven4Tools.Notifications.v1).

.DESCRIPTION
notifications.json раздаётся лаунчеру с raw.githubusercontent.com без
какого-либо контроля целостности со стороны хостинга — подпись даёт
независимый от GitHub-инфраструктуры корень доверия (аналог version.json,
см. deploy-version-manifest.ps1). Приватный ключ создаётся и хранится
офлайн, никогда не коммитится в репозиторий.

После правки Catalog/notifications.json запустить этот скрипт, затем
закоммитить И notifications.json, И обновлённый notifications.json.sig —
несовпадающая пара (старая подпись + новый текст) отклоняется
NotificationsVerifier.Verify так же, как и полное отсутствие подписи.

.EXAMPLE
.\Tools\sign-notifications.ps1
#>
param(
    [string]$NotificationsJsonPath = "$PSScriptRoot\..\Catalog\notifications.json",
    [string]$PrivateKeyPath = "$env:USERPROFILE\.ven4tools\notifications-signing-private.pem",
    [string]$SignerDll = "$PSScriptRoot\NotificationsSigner\bin\Release\net8.0\NotificationsSigner.dll"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $NotificationsJsonPath)) { throw "Не найден $NotificationsJsonPath" }
if (-not (Test-Path $PrivateKeyPath)) {
    throw "Не найден приватный ключ подписи уведомлений: $PrivateKeyPath. " +
          "Ключ не хранится в репозитории — он должен быть на этой машине отдельно."
}
if (-not (Test-Path $SignerDll)) {
    Write-Host "NotificationsSigner не собран — собираю..."
    dotnet build "$PSScriptRoot\NotificationsSigner\NotificationsSigner.csproj" -c Release --nologo | Out-Null
}

$sigPath = "$NotificationsJsonPath.sig"
Remove-Item $sigPath -ErrorAction SilentlyContinue

Write-Host "Подписываю $NotificationsJsonPath..."
dotnet $SignerDll $NotificationsJsonPath $PrivateKeyPath
if (-not (Test-Path $sigPath)) { throw "Подпись не создана — проверь вывод NotificationsSigner выше." }

Write-Host "Готово. Закоммитьте notifications.json и notifications.json.sig вместе."
