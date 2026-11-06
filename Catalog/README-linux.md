# Каталог Ven4Tools для Linux

`linux-catalog.json` — отдельный каталог Linux-версии. Он не зависит от `master.json`:
в нём только программы, которые реально ставятся в Linux, плюс программы, которых
в Windows-каталоге нет (Flatseal, Bottles, Lutris и др.).

## Как устроен

- `kind: "ven4tools-linux-catalog"` — признак типа. Оба каталога подписаны одним ключом,
  и по этому полю программа не примет `master.json` вместо каталога Linux и наоборот.
- `version` — своя нумерация. Каталог с версией ниже уже принятой программа отвергает (защита от отката).
- Подпись — `linux-catalog.json.sig`, тот же ECDSA-ключ и та же проверка, что у `master.json`
  (`Ven4Tools/Services/CatalogSignatureVerifier.cs`). Файлы помечены `-text` в `.gitattributes`:
  подпись посчитана над байтами с LF-окончаниями.
- Программа берёт каталог по очереди: `ven4tools.ru/catalog/linux-catalog.json` → `cdn.ven4tools.ru/linux-catalog.json`
  → GitHub (`Catalog/linux-catalog.json` в `main`), затем сохранённую копию, затем встроенную.
  Если каталог Linux не прошёл проверку нигде, программа работает по-старому: `master.json` + `linux/Ven4Tools.Linux/linux-packages.json`.

Поля программы:

| Поле | Значение |
|---|---|
| `id` | свой id; для программ из Windows-каталога совпадает с id в `master.json` |
| `name`, `nameEn` | название; `nameEn` — если в английском интерфейсе оно другое |
| `category` | категории Windows-каталога + «Система Linux» |
| `description`, `descriptionEn` | описание |
| `flatpak` / `snap`, `snapPublisher`, `snapClassic` | откуда ставится (Flatpak в приоритете) |
| `publisher` | издатель пакета (на Flathub — `developer_name`) |
| `official` | пакет выпускает сам автор программы (на Flathub — подтверждённый разработчик) |
| `replaces` | какие программы Windows-каталога она заменяет (`peazip` → `7zip`). По нему работают готовые наборы клиента Windows, избранное и профиль, перенесённые из Windows |
| `status` | `ok` или `unavailable` — последняя проверка не прошла, программа показывается приглушённой |
| `checked` | дата и результат последней проверки: `install-ok`, `api-ok`, `snap-info-ok` или причина отказа |

## Как добавить программу

1. Найти пакет на Flathub, проверить: `https://flathub.org/api/v2/appstream/<id>` отвечает 200,
   `is_eol` — false, издатель — тот, кого ждём.
2. Добавить запись в `apps` (порядок — по `id`). Если программа заменяет программу из Windows-каталога, указать `replaces`.
3. Прогнать проверку (ниже) хотя бы на этой программе.
4. Поднять `version`, подписать:
   `dotnet run --project Tools/CatalogSigner -c Release -- Catalog/linux-catalog.json ~/.ven4tools/catalog-signing-private.pem`
5. Выложить на сайт и CDN рядом с `master.json` (`linux-catalog.json` + `.sig`) и закоммитить.

## Честная проверка

`Tools/LinuxCatalog/linux_catalog_check.py`:

- Flatpak — данные Flathub (пакет есть, не снят с поддержки, подтверждённый разработчик не пропал),
  затем `flatpak install --user` на чистой системе, `flatpak info`, удаление;
- Snap — данные магазина Snap (пакет есть, издатель тот же).

Не прошла — `status: unavailable` с причиной в `checked.result`. Если площадка просто не ответила,
статус не меняется. Если статус хотя бы одной программы изменился, скрипт поднимает `version`.
Подпись скрипт не ставит.

- CI: `.github/workflows/linux-catalog-check.yml` — каждый день и вручную (можно указать только нужные id).
  Результат — артефакт `linux-catalog-check`: обновлённый каталог и отчёт `linux-catalog-report.md`.
- Локально или в ВМ:
  `python3 Tools/LinuxCatalog/linux_catalog_check.py --catalog Catalog/linux-catalog.json --out /tmp/lc.json --report /tmp/lc.md [--only id1,id2] [--limit 10] [--no-install]`

После проверки владелец смотрит отчёт, переносит обновлённый каталог в `Catalog/`, подписывает и выкладывает.
