# Деплой релиза на CDN cdn.ven4tools.ru.
# Загружает master.json и zip клиента на VPS, обновляет version.json.
#
# Перед запуском (обязательно):
#   $env:CDN_VPS_HOST = '138.16.152.133'
#   $env:CDN_VPS_USER = 'root'
#   $env:CDN_VPS_PWD  = 'пароль_от_vps'
#
# Опционально (рекомендуется): $env:CDN_VPS_HOSTKEY = 'ssh-ed25519 AAAA...'
# Получить: ssh-keyscan -t ed25519 138.16.152.133
# Если задан — host key VPS проверяется, иначе соединение прерывается.
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

# --- Параметры VPS (только через env, без хардкода) ---
$VpsHost = $env:CDN_VPS_HOST
$VpsUser = $env:CDN_VPS_USER
$VpsPwd  = $env:CDN_VPS_PWD
if (-not $VpsHost) { Write-Host "ОШИБКА: CDN_VPS_HOST не задан" -ForegroundColor Red; exit 1 }
if (-not $VpsUser) { Write-Host "ОШИБКА: CDN_VPS_USER не задан" -ForegroundColor Red; exit 1 }
if (-not $VpsPwd) {
    Write-Host "ОШИБКА: не задана переменная CDN_VPS_PWD" -ForegroundColor Red
    Write-Host "Установите: `$env:CDN_VPS_PWD = 'пароль'" -ForegroundColor Yellow
    exit 1
}

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
    Write-Host "Сначала соберите релиз (см. CLAUDE.md, раздел 'Сборка клиента')." -ForegroundColor Yellow
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
$env:CDN_HOSTKEY       = $env:CDN_VPS_HOSTKEY
$env:CDN_MASTER        = $MasterJson
$env:CDN_ZIP           = $ClientZip
$env:CDN_VERSION       = $Version
$env:CDN_LVERSION      = $LauncherVersion

$pyCode = @'
import os, sys, json, hashlib, base64
import paramiko

host  = os.environ["CDN_HOST"]
user  = os.environ["CDN_USER"]
pwd   = os.environ["CDN_PWD"]
master = os.environ["CDN_MASTER"]
zip_path = os.environ["CDN_ZIP"]
ver   = os.environ["CDN_VERSION"]
lver  = os.environ["CDN_LVERSION"]

zip_name = os.path.basename(zip_path)

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(65536), b''):
            h.update(chunk)
    return h.hexdigest()

zip_sha256 = sha256_file(zip_path)
print("SHA256 zip:", zip_sha256)

cli = paramiko.SSHClient()

# Проверка host key VPS. Если CDN_VPS_HOSTKEY задан — соединение пройдёт
# только при совпадении ключа (защита от MITM). Формат: "ssh-ed25519 AAAA..."
# Получить один раз: ssh-keyscan -t ed25519 <host>
known_hosts_line = os.environ.get("CDN_HOSTKEY", "")
if known_hosts_line:
    parts = known_hosts_line.split(None, 1)
    key_type, key_b64 = parts[0], parts[1]
    host_keys = cli.get_host_keys()
    key_bytes = base64.b64decode(key_b64)
    if key_type == "ssh-rsa":
        host_keys.add(host, key_type, paramiko.RSAKey(data=key_bytes))
    elif key_type == "ssh-ed25519":
        host_keys.add(host, key_type, paramiko.Ed25519Key(data=key_bytes))
    cli.set_missing_host_key_policy(paramiko.RejectPolicy())
else:
    # HOSTKEY не задан — принимаем (для первого запуска), но предупреждаем.
    cli.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    print("ПРЕДУПРЕЖДЕНИЕ: CDN_VPS_HOSTKEY не задан, host key не проверяется")

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
        "zip_fallback": f"https://github.com/Ven4ru/Ven4Tools/releases/download/v{ver}/Ven4Tools-Client-{ver}.zip",
        "zip_sha256": zip_sha256
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
