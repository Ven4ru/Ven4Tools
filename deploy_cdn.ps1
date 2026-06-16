# Деплой релиза на CDN cdn.ven4tools.ru.
# Загружает master.json и zip клиента на VPS, обновляет version.json.
#
# Использование:
#   .\deploy_cdn.ps1 -Version 3.4.5
#   .\deploy_cdn.ps1 -Version 3.4.5 -LauncherVersion 2.0.0
#
# Требуется: Python 3 + paramiko (уже установлены).

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # Версия лаунчера в version.json (по умолчанию текущая на CDN — 2.0.0).
    [string]$LauncherVersion = "2.0.0"
)

$ErrorActionPreference = "Stop"

# --- Параметры VPS ---
$VpsHost = "138.16.152.133"
$VpsUser = "root"
$VpsPwd  = "***REMOVED***"

# --- Локальные пути ---
$RepoRoot   = $PSScriptRoot
$MasterJson = Join-Path $RepoRoot "Catalog\master.json"
$ClientZip  = Join-Path $RepoRoot "_release\Ven4Tools-Client-$Version.zip"

# --- Проверки ---
if (-not (Test-Path $MasterJson)) {
    Write-Host "ОШИБКА: не найден master.json: $MasterJson" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $ClientZip)) {
    Write-Host "ОШИБКА: не найден zip клиента: $ClientZip" -ForegroundColor Red
    Write-Host "Сначала соберите релиз (см. документацию по сборке клиента)." -ForegroundColor Yellow
    exit 1
}

$python = (Get-Command python3 -ErrorAction SilentlyContinue)
if ($null -eq $python) { $python = (Get-Command python -ErrorAction SilentlyContinue) }
if ($null -eq $python) {
    Write-Host "ОШИБКА: не найден Python 3 в PATH." -ForegroundColor Red
    exit 1
}

Write-Host "=== Деплой Ven4Tools на CDN ===" -ForegroundColor Cyan
Write-Host "Клиент:  $Version"
Write-Host "Лаунчер: $LauncherVersion"
Write-Host "Zip:     $ClientZip"
Write-Host ""

# Инлайн-скрипт Python: SFTP-загрузка + обновление version.json.
# Пути и параметры передаём через переменные окружения (без интерполяции в код).
$env:CDN_HOST          = $VpsHost
$env:CDN_USER          = $VpsUser
$env:CDN_PWD           = $VpsPwd
$env:CDN_MASTER        = $MasterJson
$env:CDN_ZIP           = $ClientZip
$env:CDN_VERSION       = $Version
$env:CDN_LVERSION      = $LauncherVersion

$pyCode = @'
import os, sys, json
import paramiko

host  = os.environ["CDN_HOST"]
user  = os.environ["CDN_USER"]
pwd   = os.environ["CDN_PWD"]
master = os.environ["CDN_MASTER"]
zip_path = os.environ["CDN_ZIP"]
ver   = os.environ["CDN_VERSION"]
lver  = os.environ["CDN_LVERSION"]

zip_name = os.path.basename(zip_path)

cli = paramiko.SSHClient()
cli.set_missing_host_key_policy(paramiko.AutoAddPolicy())
cli.connect(host, username=user, password=pwd, timeout=20)

# Гарантируем структуру папок
cli.exec_command("mkdir -p /var/www/cdn/releases")[1].channel.recv_exit_status()

sftp = cli.open_sftp()

# 1. master.json
print("Загрузка master.json...")
sftp.put(master, "/var/www/cdn/master.json")

# 2. zip клиента
print(f"Загрузка {zip_name}...")
sftp.put(zip_path, f"/var/www/cdn/releases/{zip_name}")

# 3. version.json (обновляем секцию client; launcher сохраняем/обновляем версию)
version_obj = {
    "client": {
        "version": ver,
        "zip_url": f"https://cdn.ven4tools.ru/releases/Ven4Tools-Client-{ver}.zip",
        "zip_fallback": f"https://github.com/Ven4ru/Ven4Tools/releases/download/v{ver}/Ven4Tools-Client-{ver}.zip"
    },
    "launcher": {
        "version": lver,
        "exe_url": f"https://cdn.ven4tools.ru/releases/Ven4Tools.Launcher-{lver}.exe",
        "exe_fallback": f"https://github.com/Ven4ru/Ven4Tools/releases/download/launcher-v{lver}/Ven4Tools.Launcher-{lver}.exe",
        "setup_url": f"https://cdn.ven4tools.ru/releases/Ven4Tools.Setup-{lver}.exe",
        "setup_fallback": f"https://github.com/Ven4ru/Ven4Tools/releases/download/launcher-v{lver}/Ven4Tools.Setup-{lver}.exe"
    }
}
import datetime
version_obj["updated_at"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

print("Обновление version.json...")
with sftp.open("/var/www/cdn/version.json", "w") as f:
    f.write(json.dumps(version_obj, ensure_ascii=False, indent=2))

sftp.close()

# Права на свежие файлы
cli.exec_command("chown -R www-data:www-data /var/www/cdn && chmod -R 755 /var/www/cdn")[1].channel.recv_exit_status()

cli.close()
print("Готово. version.json обновлён на версию клиента", ver)
'@

# Выполняем Python-скрипт через stdin
$pyCode | & $python.Source -
$exit = $LASTEXITCODE

# Чистим переменные окружения (чтобы пароль не остался в сессии)
Remove-Item Env:CDN_PWD -ErrorAction SilentlyContinue

if ($exit -ne 0) {
    Write-Host "ОШИБКА деплоя (код $exit)" -ForegroundColor Red
    exit $exit
}

Write-Host ""
Write-Host "=== Деплой завершён успешно ===" -ForegroundColor Green
Write-Host "Проверка: https://cdn.ven4tools.ru/version.json"
