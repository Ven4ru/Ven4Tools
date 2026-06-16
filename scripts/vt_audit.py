"""
Аудит SHA256 из Catalog/master.json через VirusTotal API.
Регистрация и ключ: https://www.virustotal.com/gui/my-apikey

Использование:
    $env:VT_API_KEY = "YOUR_VT_API_KEY"; python scripts/vt_audit.py
    python scripts/vt_audit.py --only-missing
    python scripts/vt_audit.py --key YOUR_VT_API_KEY   # ключ можно передать и явно

Ключ берётся из переменной окружения VT_API_KEY либо из аргумента --key.

Free tier: 4 запроса/мин, 500/день.
Скрипт автоматически соблюдает лимит.
"""
import argparse
import json
import os
import sys
import time
from pathlib import Path

try:
    import requests
except ImportError:
    print("Установи requests: pip install requests")
    sys.exit(1)

CATALOG_PATH = Path(__file__).parent.parent / "Catalog" / "master.json"
VT_URL = "https://www.virustotal.com/api/v3/files/{}"
RATE_LIMIT_DELAY = 15  # секунд между запросами (4/мин = 15с интервал)


def check_hash(sha256: str, api_key: str) -> dict:
    resp = requests.get(
        VT_URL.format(sha256),
        headers={"x-apikey": api_key},
        timeout=15
    )
    if resp.status_code == 404:
        return {"status": "not_found"}
    if resp.status_code == 429:
        return {"status": "rate_limited"}
    if resp.status_code != 200:
        return {"status": "error", "code": resp.status_code}

    data = resp.json().get("data", {})
    stats = data.get("attributes", {}).get("last_analysis_stats", {})
    malicious = stats.get("malicious", 0)
    suspicious = stats.get("suspicious", 0)
    total = sum(stats.values())
    return {
        "status": "ok",
        "malicious": malicious,
        "suspicious": suspicious,
        "total": total,
        "clean": malicious == 0 and suspicious == 0,
    }


def main():
    parser = argparse.ArgumentParser(description="VirusTotal аудит каталога")
    parser.add_argument("--key", default=None,
                        help="VT API ключ (по умолчанию берётся из VT_API_KEY)")
    parser.add_argument("--only-missing", action="store_true",
                        help="Показывать только приложения без SHA256")
    args = parser.parse_args()

    api_key = args.key or os.environ.get("VT_API_KEY")
    if not api_key:
        print("ОШИБКА: не задан VT API ключ.")
        print("Установите переменную окружения:")
        print('  $env:VT_API_KEY = "YOUR_VT_API_KEY"   # PowerShell')
        print("или передайте аргументом: --key YOUR_VT_API_KEY")
        sys.exit(1)

    with open(CATALOG_PATH, encoding="utf-8") as f:
        catalog = json.load(f)

    apps = catalog["apps"]
    to_check = [a for a in apps if a.get("sha256") and not a.get("skipHash")]
    missing = [a for a in apps if not a.get("sha256") and not a.get("skipHash")]
    skip_hash = [a for a in apps if a.get("skipHash")]

    print(f"\nКаталог: {len(apps)} приложений")
    print(f"  Есть SHA256: {len(to_check)}")
    print(f"  Нет SHA256:  {len(missing)}")
    print(f"  skipHash:    {len(skip_hash)}")

    if missing:
        print("\n⚠️  Приложения без SHA256 (skipHash не выставлен):")
        for a in missing:
            print(f"  - {a['name']} ({a['id']})")

    if args.only_missing:
        return

    if not to_check:
        print("\nНечего проверять.")
        return

    print(f"\n🔍 Проверяю {len(to_check)} хешей через VirusTotal...")
    print(f"   Ожидаемое время: ~{len(to_check) * RATE_LIMIT_DELAY // 60} мин\n")

    clean = []
    flagged = []
    not_found = []
    errors = []

    for i, app in enumerate(to_check, 1):
        name = app["name"]
        sha = app["sha256"]
        print(f"[{i:2}/{len(to_check)}] {name}...", end=" ", flush=True)

        result = check_hash(sha, api_key)

        if result["status"] == "not_found":
            print("❓ не найден в VT")
            not_found.append(app)
        elif result["status"] in ("rate_limited", "error"):
            print(f"❌ ошибка ({result['status']})")
            errors.append(app)
        elif result["clean"]:
            print(f"✅ чисто ({result['malicious']}/{result['total']})")
            clean.append(app)
        else:
            print(f"🚨 ФЛАГИ: {result['malicious']} malicious, {result['suspicious']} suspicious")
            flagged.append(app)

        if i < len(to_check):
            time.sleep(RATE_LIMIT_DELAY)

    print(f"\n{'='*50}")
    print(f"✅ Чисто:       {len(clean)}")
    print(f"🚨 С флагами:   {len(flagged)}")
    print(f"❓ Не в VT:     {len(not_found)}")
    print(f"❌ Ошибки:      {len(errors)}")

    if flagged:
        print("\n🚨 ТРЕБУЮТ ВНИМАНИЯ:")
        for a in flagged:
            print(f"  {a['name']} — {a['sha256']}")

    if not_found:
        print("\n❓ Не найдены в VT (загрузить вручную):")
        for a in not_found:
            print(f"  {a['name']} — {a['sha256']}")


if __name__ == "__main__":
    main()
