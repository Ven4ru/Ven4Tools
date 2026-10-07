<#
.SYNOPSIS
    Кладёт языковой пакет рядом с релизным файлом — под именем, под которым он публикуется.

.DESCRIPTION
    Английский перевод поставляется отдельным файлом (Localization\packs\<программа>-en.json),
    а его контрольная сумма вшита в программу при сборке: программа примет только пакет
    своей сборки. Поэтому выкладывать нужно ровно тот файл, с которым собран релиз.

    Скрипт сверяет это по факту: ищет контрольную сумму пакета внутри собранной программы
    (Ven4Tools.dll клиента или Ven4Tools.Launcher.exe лаунчера). Нашлась — пакет копируется
    в папку релиза как Ven4Tools-Client-X.Y.Z-lang-en.json либо
    Ven4Tools-Launcher-X.Y.Z-lang-en.json. Не нашлась — ошибка: исходники перевода
    изменились после сборки, программу нужно пересобрать.

    Дальше файл выкладывает Tools\deploy-cdn-release.ps1 (он берёт его рядом с архивом
    клиента или установщиком), и его же нужно приложить к релизу на GitHub.

.PARAMETER Component
    client или launcher.

.PARAMETER Version
    Версия релиза, X.Y.Z.

.PARAMETER Binary
    Собранная программа: Ven4Tools.dll из папки публикации клиента либо
    Ven4Tools.Launcher.exe из папки публикации лаунчера.

.PARAMETER OutDir
    Куда положить пакет. По умолчанию — _release в корне репозитория.

.EXAMPLE
    .\Tools\export-language-pack.ps1 -Component client -Version 6.1.0 -Binary .\_release\client_610\Ven4Tools.dll
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('client', 'launcher')][string]$Component,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$Binary,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\_release')
)

$ErrorActionPreference = 'Stop'

$pack = Join-Path $PSScriptRoot "..\Localization\packs\$Component-en.json"
if (-not (Test-Path -LiteralPath $pack)) { throw "Языковой пакет не найден: $pack" }
if (-not (Test-Path -LiteralPath $Binary)) { throw "Собранная программа не найдена: $Binary" }

$sha = (Get-FileHash -LiteralPath $pack -Algorithm SHA256).Hash.ToUpperInvariant()

# Строковая константа лежит в сборке .NET в кодировке UTF-16LE.
$needle = [System.Text.Encoding]::Unicode.GetBytes($sha)
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Binary).Path)
$found = $false
$first = $needle[0]
$last = $bytes.Length - $needle.Length
$index = 0
while (-not $found) {
    $index = [Array]::IndexOf($bytes, $first, $index)
    if ($index -lt 0 -or $index -gt $last) { break }
    $match = $true
    for ($i = 1; $i -lt $needle.Length; $i++) {
        if ($bytes[$index + $i] -ne $needle[$i]) { $match = $false; break }
    }
    if ($match) { $found = $true } else { $index++ }
}
if (-not $found) {
    throw "Контрольная сумма пакета ($sha) не найдена в ${Binary}: перевод изменился после сборки. " +
          'Пересоберите пакеты (dotnet run --project Tools\LanguagePackTool -- pack .) и программу.'
}

$kind = if ($Component -eq 'launcher') { 'Launcher' } else { 'Client' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$target = Join-Path $OutDir "Ven4Tools-$kind-$Version-lang-en.json"
Copy-Item -LiteralPath $pack -Destination $target -Force

Write-Host "Языковой пакет соответствует сборке и готов к выкладке: $target" -ForegroundColor Green
Write-Host "sha256 $($sha.ToLowerInvariant())"
