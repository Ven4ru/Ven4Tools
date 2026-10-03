<#
.SYNOPSIS
    Сверяет правила доверия к источнику загрузки в install.ps1 с общим набором примеров.

.DESCRIPTION
    install.ps1 повторяет правило лаунчера (DownloadValidator.IsAllowedUri) своей
    функцией Test-AllowedSource: вызвать код лаунчера скрипт не может. Чтобы копии не
    расходились, обе проверяются одним набором примеров —
    tests/Ven4Tools.Tests/Fixtures/download-trust-vectors.json: лаунчер — тестом
    DownloadTrustVectorsTests, скрипт — здесь.

    Функция извлекается из install.ps1 разбором, сам скрипт не выполняется.
    Код возврата 1 — есть расхождения.
#>
param(
    [string]$InstallScript = (Join-Path $PSScriptRoot '..\..\install.ps1'),
    [string]$Vectors = (Join-Path $PSScriptRoot '..\Ven4Tools.Tests\Fixtures\download-trust-vectors.json')
)

$ErrorActionPreference = 'Stop'

$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path $InstallScript).Path, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw "install.ps1 не разбирается: $($errors[0].Message)" }

$function = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-AllowedSource'
    }, $true)
if (-not $function) { throw 'В install.ps1 не найдена функция Test-AllowedSource.' }
. ([scriptblock]::Create($function.Extent.Text))

$items = Get-Content -LiteralPath $Vectors -Raw -Encoding UTF8 | ConvertFrom-Json
$wrong = @()
foreach ($item in $items) {
    $actual = $false
    try { $actual = [bool](Test-AllowedSource ([uri]$item.url)) } catch { $actual = $false }
    if ($actual -ne [bool]$item.installScript) {
        $wrong += "  $($item.url) — ожидалось $($item.installScript), получено $actual"
    }
}

Write-Host "Проверено примеров: $(@($items).Count)"
if ($wrong.Count -gt 0) {
    Write-Host "Расхождение install.ps1 с набором примеров:" -ForegroundColor Red
    $wrong | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host 'Правила install.ps1 совпадают с набором примеров.' -ForegroundColor Green
