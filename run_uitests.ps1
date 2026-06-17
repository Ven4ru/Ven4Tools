# Запуск UI-тестов клиента Ven4Tools
# Использование: .\run_uitests.ps1
# Требования: запускать от имени администратора, активный рабочий стол

param(
    [switch]$NoBuild  # пропустить сборку, если уже собрано
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "=== Ven4Tools UI Tests ===" -ForegroundColor Cyan

# 1. Сборка клиента
if (-not $NoBuild) {
    Write-Host "`n[1/3] Сборка клиента..." -ForegroundColor Yellow
    dotnet build "$root\Ven4Tools\Ven4Tools.csproj" --nologo 2>&1 | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА сборки клиента." -ForegroundColor Red; exit 1 }

    Write-Host "[2/3] Сборка UITests..." -ForegroundColor Yellow
    dotnet build "$root\Ven4Tools.UITests\Ven4Tools.UITests.csproj" -p:Platform=x64 --nologo 2>&1 | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) { Write-Host "ОШИБКА сборки UITests." -ForegroundColor Red; exit 1 }
} else {
    Write-Host "[1-2/3] Сборка пропущена (-NoBuild)." -ForegroundColor DarkGray
}

# 2. Закрыть все запущенные экземпляры клиента (могут помешать тестам)
Get-Process -Name "Ven4Tools" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 3. Запуск тестов
Write-Host "`n[3/3] Запуск UI-тестов..." -ForegroundColor Yellow
$result = dotnet test "$root\Ven4Tools.UITests\Ven4Tools.UITests.csproj" `
    -p:Platform=x64 --no-build `
    --logger "console;verbosity=normal" 2>&1

$result | Write-Output

# 4. Итог
$passed  = ($result | Select-String "Пройдено:\s+(\d+)").Matches.Groups[1].Value
$failed  = ($result | Select-String "Не пройдено:\s+(\d+)").Matches.Groups[1].Value
$skipped = ($result | Select-String "Пропущено:\s+(\d+)").Matches.Groups[1].Value

Write-Host "`n=== Итог ===" -ForegroundColor Cyan
Write-Host "  Пройдено:    $passed" -ForegroundColor Green
if ($failed -and $failed -ne "0") {
    Write-Host "  Не пройдено: $failed" -ForegroundColor Red
}
if ($skipped -and $skipped -ne "0") {
    Write-Host "  Пропущено:   $skipped (Inconclusive — скрытые вкладки)" -ForegroundColor DarkGray
}

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nВсе тесты прошли. Можно делать релиз." -ForegroundColor Green
} else {
    Write-Host "`nЕсть упавшие тесты — проверить до релиза." -ForegroundColor Red
    exit 1
}
