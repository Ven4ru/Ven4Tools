#!/usr/bin/env python3
"""Честная проверка каталога Ven4Tools для Linux.

Каждая программа каталога проверяется на чистой системе:
  * Flatpak — данные Flathub (есть ли пакет, не снят ли с поддержки, подтверждён ли разработчик),
    затем настоящая установка `flatpak install --user`, проверка `flatpak info` и удаление;
  * Snap — данные магазина Snap (есть ли пакет, кто издатель).
По итогам у каждой программы обновляются `status` (ok / unavailable) и `checked`,
пишется отчёт в Markdown. Подпись каталога скрипт не ставит: её ставит владелец
(Tools/CatalogSigner) после просмотра отчёта.

Запуск в CI — .github/workflows/linux-catalog-check.yml. Локально (в ВМ):
  python3 linux_catalog_check.py --catalog linux-catalog.json --out linux-catalog.checked.json \
      --report linux-catalog-report.md [--only id1,id2] [--limit 10] [--no-install]
"""
import argparse
import datetime as dt
import json
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

UA = {'User-Agent': 'Ven4Tools-linux-catalog-check'}


def http_json(url, headers=None, tries=4):
    """(код ответа, json или None). 0 — сеть не ответила."""
    h = dict(UA, **(headers or {}))
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=h), timeout=30) as r:
                return r.status, json.load(r)
        except urllib.error.HTTPError as e:
            return e.code, None
        except Exception:
            time.sleep(2 + attempt * 3)
    return 0, None


def run(cmd, timeout=1800):
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
        return p.returncode, (p.stdout + p.stderr).strip()
    except subprocess.TimeoutExpired:
        return 124, 'timeout'
    except FileNotFoundError:
        return 127, cmd[0] + ' not found'


def free_gb(path='/'):
    try:
        return shutil.disk_usage(path).free / 2 ** 30
    except Exception:
        return 99.0


def check_flatpak(app, install):
    fid = app['flatpak']
    code, d = http_json(f'https://flathub.org/api/v2/appstream/{fid}?locale=en')
    if code == 404:
        return 'unavailable', 'нет на Flathub (404)', {}
    if code != 200 or d is None:
        return None, f'Flathub не ответил ({code})', {}
    md = d.get('metadata') or {}
    info = {'developer': d.get('developer_name') or '', 'verified': bool(md.get('flathub::verification::verified'))}
    if d.get('is_eol'):
        return 'unavailable', 'снят с поддержки на Flathub (EOL)', info
    if app.get('official') and not info['verified']:
        # Подтверждённый автором пакет перестал быть подтверждённым — возможна смена владельца
        return 'unavailable', 'разработчик на Flathub больше не подтверждён', info
    if not install:
        return 'ok', 'api-ok', info
    if free_gb() < 4:
        run(['flatpak', 'uninstall', '--user', '--unused', '-y', '--noninteractive'])
    rc, out = run(['flatpak', 'install', '--user', '-y', '--noninteractive', 'flathub', fid])
    if rc != 0:
        tail = out.splitlines()[-1] if out else ''
        return 'unavailable', f'установка не прошла: {tail[:160]}', info
    rc, out = run(['flatpak', 'info', '--user', fid], timeout=120)
    installed = rc == 0
    run(['flatpak', 'uninstall', '--user', '-y', '--noninteractive', fid])
    if not installed:
        return 'unavailable', 'после установки пакет не найден (flatpak info)', info
    return 'ok', 'install-ok', info


def check_snap(app):
    name = app['snap']
    code, d = http_json(f'https://api.snapcraft.io/v2/snaps/info/{name}',
                        headers={'Snap-Device-Series': '16'})
    if code == 404:
        return 'unavailable', 'нет в магазине Snap (404)', {}
    if code != 200 or d is None:
        return None, f'магазин Snap не ответил ({code})', {}
    pub = (d.get('snap') or {}).get('publisher') or {}
    info = {'developer': pub.get('display-name') or pub.get('username') or ''}
    expected = app.get('snapPublisher')
    if expected and info['developer'] and info['developer'] != expected:
        return 'unavailable', f'сменился издатель Snap: {info["developer"]} (было {expected})', info
    return 'ok', 'snap-info-ok', info


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--catalog', required=True)
    ap.add_argument('--out', required=True, help='куда записать каталог с обновлёнными status/checked')
    ap.add_argument('--report', required=True)
    ap.add_argument('--only', default='', help='id через запятую')
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--no-install', action='store_true', help='только данные Flathub/Snap, без установки')
    ap.add_argument('--date', default=dt.date.today().isoformat())
    a = ap.parse_args()

    raw = open(a.catalog, encoding='utf-8').read()
    cat = json.loads(raw)
    apps = cat['apps']
    only = {x.strip() for x in a.only.split(',') if x.strip()}
    todo = [x for x in apps if not only or x['id'] in only]
    if a.limit:
        todo = todo[:a.limit]
    install = not a.no_install
    if install:
        rc, out = run(['flatpak', 'remote-add', '--user', '--if-not-exists', 'flathub',
                       'https://dl.flathub.org/repo/flathub.flatpakrepo'])
        if rc != 0:
            print('flatpak remote-add не прошёл: ' + out, file=sys.stderr)
            return 2

    rows, changed = [], False
    for i, x in enumerate(todo, 1):
        t0 = time.time()
        if x.get('flatpak'):
            status, result, info = check_flatpak(x, install)
            source = 'Flathub · ' + x['flatpak']
        elif x.get('snap'):
            status, result, info = check_snap(x)
            source = 'Snap · ' + x['snap']
        else:
            status, result, info = 'unavailable', 'нет ни Flatpak, ни Snap', {}
            source = '—'
        note = ''
        if info.get('developer') and x.get('publisher') and info['developer'] != x['publisher']:
            note = f'издатель сейчас: {info["developer"]}'
        if status is None:
            # Площадка не ответила — прежний статус не трогаем, это не повод прятать программу
            rows.append((x['id'], x['name'], source, x.get('status', 'ok'), result + ' — статус не менялся', note))
            print(f'[{i}/{len(todo)}] {x["id"]}: пропуск ({result})', flush=True)
            continue
        if x.get('status') != status:
            changed = True
        x['status'] = status
        x['checked'] = {'date': a.date, 'result': result}
        rows.append((x['id'], x['name'], source, status, result, note))
        print(f'[{i}/{len(todo)}] {x["id"]}: {status} ({result}) {time.time() - t0:.0f} c', flush=True)

    if changed:
        # Смена статуса — новая версия каталога; программа отвергает каталог с версией ниже принятой
        cat['version'] = int(cat.get('version', 0)) + 1
    cat['lastUpdated'] = a.date
    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(json.dumps(cat, ensure_ascii=False, indent=2) + '\n')

    ok = sum(1 for r in rows if r[3] == 'ok')
    bad = [r for r in rows if r[3] != 'ok']
    lines = [
        f'# Проверка каталога Linux — {a.date}',
        '',
        f'Проверено: {len(rows)} из {len(apps)}. Режим: {"установка на чистой системе" if install else "только данные Flathub/Snap"}.',
        f'Итог: ok — {ok}, недоступны — {len(bad)}. Версия каталога после проверки: {cat["version"]}'
        + (' (статусы изменились — нужна новая подпись)' if changed else ' (статусы не изменились)') + '.',
        '',
    ]
    if bad:
        lines += ['## Недоступны', '', '| id | Программа | Источник | Причина |', '|---|---|---|---|']
        lines += [f'| {r[0]} | {r[1]} | {r[2]} | {r[4]} |' for r in bad]
        lines.append('')
    lines += ['## Все проверенные', '', '| id | Программа | Источник | Статус | Результат | Заметка |',
              '|---|---|---|---|---|---|']
    lines += [f'| {r[0]} | {r[1]} | {r[2]} | {r[3]} | {r[4]} | {r[5]} |' for r in rows]
    with open(a.report, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print(f'ok={ok} unavailable={len(bad)} changed={changed}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
