<#
.SYNOPSIS
Готовит блочное (дельта-) обновление клиента: строит файловый манифест публикации,
подписывает его ECDSA-ключом (Ven4Tools.ClientManifest.v1) и заливает на CDN вместе
с самими файлами публикации.

.DESCRIPTION
Манифест client-manifest.json — список «относительный путь → SHA256 → размер» для
каждого файла опубликованной версии клиента. По нему лаунчер качает при обновлении
только изменившиеся файлы вместо zip-архива целиком (на типичном релизе это
несколько файлов из нескольких сотен).

Манифест задаёт, какие ОТДЕЛЬНЫЕ файлы лаунчер положит внутрь папки установленного
клиента, поэтому подпись здесь не менее важна, чем у version.json, и ключ у неё
отдельный: приватная половина никогда не покидает эту машину и не оказывается на CDN.

После заливки обязательно допишите в version.json блок client:
    "manifest_url":              "https://cdn.ven4tools.ru/client-files/<версия>/client-manifest.json",
    "manifest_signature_url":    "https://cdn.ven4tools.ru/client-files/<версия>/client-manifest.json.sig",
    "files_base_url":            "https://cdn.ven4tools.ru/client-files/<версия>/",
    "files_base_mirror_hosting": "https://ven4tools.ru/releases/client-files/<версия>/"
и выложите version.json скриптом deploy-version-manifest.ps1. Пока этих полей нет,
лаунчер просто обновляется полным путём — это штатное поведение, а не поломка.

ВАЖНО: файлы публикации на CDN должны быть теми же самыми, что внутри zip-архива
релиза, — манифест описывает содержимое архива. Раскладывайте на CDN ровно ту папку
публикации, из которой собран архив.

.PARAMETER PublishPath
Папка публикации клиента (та, что упакована в Ven4Tools-Client-<версия>.zip).

.PARAMETER Version
Версия клиента, например 5.1.0. Используется и в манифесте, и в пути на CDN.

.EXAMPLE
.\Tools\deploy-client-manifest.ps1 -PublishPath .\_release\publish -Version 5.1.0
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PrivateKeyPath = "$env:USERPROFILE\.ven4tools\client-manifest-signing-private.pem",
    [string]$PublicKeyPath = "$PSScriptRoot\ClientManifestSigner\client-manifest-signing-public.pem",
    [string]$SignerDll = "$PSScriptRoot\ClientManifestSigner\bin\Release\net8.0\ClientManifestSigner.dll",
    [switch]$SkipUpload
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublishPath)) { throw "Не найдена папка публикации: $PublishPath" }
if (-not (Test-Path $PrivateKeyPath)) {
    throw "Не найден приватный ключ подписи файлового манифеста: $PrivateKeyPath. " +
          "Ключ не хранится в репозитории — он должен быть на этой машине отдельно."
}
if (-not (Test-Path $SignerDll)) {
    Write-Host "ClientManifestSigner не собран — собираю..."
    dotnet build "$PSScriptRoot\ClientManifestSigner\ClientManifestSigner.csproj" -c Release --nologo | Out-Null
}

$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ven4tools-client-manifest-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $workDir | Out-Null

try {
    $manifestPath = Join-Path $workDir "client-manifest.json"
    $sigPath = "$manifestPath.sig"

    Write-Host "Строю файловый манифест публикации ($Version)..."
    dotnet $SignerDll generate $PublishPath $Version $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "Не удалось построить манифест — см. вывод выше." }

    Write-Host "Подписываю манифест..."
    dotnet $SignerDll $manifestPath $PrivateKeyPath
    if (-not (Test-Path $sigPath)) { throw "Подпись не создана — проверь вывод ClientManifestSigner выше." }

    # Самопроверка пары локально — ловит неверный ключ до того, как что-либо уйдёт
    # в прод (тот же порядок, что и в deploy-version-manifest.ps1).
    Write-Host "Проверяю подпись локально..."
    dotnet $SignerDll verify $manifestPath $sigPath $PublicKeyPath
    if ($LASTEXITCODE -ne 0) { throw "Локальная подпись не прошла проверку — заливка отменена." }

    if ($SkipUpload) {
        $kept = Join-Path (Get-Location) "client-manifest.json"
        Copy-Item $manifestPath $kept -Force
        Copy-Item $sigPath "$kept.sig" -Force
        Write-Host "SkipUpload: манифест и подпись сохранены рядом ($kept), заливка пропущена."
        return
    }

    $remoteDir = "/var/www/cdn/client-files/$Version"

    Write-Host "Заливаю файлы публикации на CDN ($remoteDir)..."
    ssh jump "mkdir -p $remoteDir"
    # Рекурсивно всю папку публикации: манифест описывает именно её содержимое,
    # и любой недолитый файл сделает дельту неработоспособной для этой версии.
    scp -r "$PublishPath/*" "jump:$remoteDir/"

    Write-Host "Заливаю манифест и подпись..."
    scp $manifestPath "jump:/tmp/client-manifest.json.new"
    scp $sigPath "jump:/tmp/client-manifest.json.sig.new"
    # mv на удалённой стороне — атомарная замена обоих файлов разом, без окна
    # «манифест уже новый, подпись ещё старая» (или наоборот).
    $remoteCmd = "mv /tmp/client-manifest.json.new $remoteDir/client-manifest.json && mv /tmp/client-manifest.json.sig.new $remoteDir/client-manifest.json.sig && chown -R root:root $remoteDir && chmod -R a+r $remoteDir"
    ssh jump $remoteCmd

    Write-Host "Проверка публичной доступности..."
    # Сверяем то, что реально отдаёт CDN, а не локальные файлы — единственный способ
    # поймать порчу байтов при заливке. Качаем -OutFile в бинарном виде: .Content —
    # это .NET string, и любая последующая запись на диск перекодировала бы её
    # (в Windows PowerShell 5.1 -Encoding utf8 добавляет BOM), ломая побайтовое
    # сравнение подписи независимо от того, корректна ли она на самом деле.
    $checkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ven4tools-client-manifest-check-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Path $checkDir | Out-Null
    try {
        $base = "https://cdn.ven4tools.ru/client-files/$Version"
        $remoteJsonFile = Join-Path $checkDir "client-manifest.json"
        $remoteSigFile = Join-Path $checkDir "client-manifest.json.sig"
        Invoke-WebRequest "$base/client-manifest.json" -OutFile $remoteJsonFile -UseBasicParsing
        Invoke-WebRequest "$base/client-manifest.json.sig" -OutFile $remoteSigFile -UseBasicParsing

        dotnet $SignerDll verify $remoteJsonFile $remoteSigFile $PublicKeyPath
        if ($LASTEXITCODE -ne 0) {
            throw "КРИТИЧНО: подпись на CDN не соответствует залитому манифесту. Проверь $remoteDir на jump-хосте немедленно."
        }

        # Выборочная проверка, что отдельные файлы публикации реально доступны по
        # тем же ссылкам, которые будет строить лаунчер: манифест без файлов рядом
        # означал бы, что дельта у всех пользователей падает и откатывается на полную
        # загрузку — молча и на каждом обновлении.
        $manifest = Get-Content $remoteJsonFile -Raw | ConvertFrom-Json
        $sample = $manifest.files | Get-Random -Count ([Math]::Min(3, $manifest.files.Count))
        foreach ($file in $sample) {
            $segments = ($file.path -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }
            $url = "$base/" + ($segments -join '/')
            $head = Invoke-WebRequest $url -Method Head -UseBasicParsing
            if ($head.StatusCode -ne 200) { throw "Файл публикации недоступен на CDN: $url" }
        }

        Write-Host "OK: манифест версии $($manifest.version), файлов $($manifest.files.Count), подпись подтверждена по данным с CDN, выборочные файлы доступны"
        Write-Host "Не забудь дописать manifest_url/manifest_signature_url/files_base_url/files_base_mirror_hosting в version.json и выложить его deploy-version-manifest.ps1."
    }
    finally {
        Remove-Item $checkDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
