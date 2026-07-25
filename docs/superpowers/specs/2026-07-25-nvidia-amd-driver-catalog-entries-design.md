# Дизайн: добавление NVIDIA App / AMD Adrenalin в каталог

## Проблема

В прошлые попытки добавить установщики видеодрайверов (NVIDIA, AMD) в каталог оба пункта
всегда показывались как «❌ Недоступно», хотя бы и через прямую ссылку, хотя бы через winget.
Это стабильно повторялось (см. `Catalog/master.json`, changelog версии 3: «Удалены GeForce
Experience и AMD Software: нет в winget и нет URL»).

## Найденная причина (подтверждено кодом и `winget search`/`choco search` на машине разработчика)

1. `winget search "NVIDIA App"` и `winget search "Adrenalin"`/`"Radeon"` не находят ничего —
   ни NVIDIA, ни AMD не публикуют драйверные пакеты в community-репозиторий winget.
2. У обоих вендоров страницы загрузки — JS-рендеринг без статичного URL на файл, то есть
   надёжной прямой ссылки тоже нет.
3. При этом в **Chocolatey** оба пакета реально существуют и поддерживаются:
   `nvidia-app` (11.0.8.299) и `amd-software-adrenalin-edition` (26.6.4).
4. Баг — в `Ven4Tools/Services/AvailabilityChecker.cs`, метод
   `CheckAppAvailabilityWithSize()` (строки 72–120): проверяет только `wingetId`
   (`GetWingetPackageInfo`) и `InstallerUrls` (`GetUrlInfo`). Про `ChocoId` там нет ни строчки —
   если у записи нет ни winget, ни прямой ссылки, статус всегда `Unavailable`, что через
   `AppRowViewModel.IsSelectable => Availability != Unavailable` (`AppRowViewModel.cs:152`)
   намертво блокирует чекбокс, даже когда реальная установка через choco прошла бы нормально.
5. Все нынешние записи каталога с `chocoId` (`snappy-driver`, `ddu`, `nvcleanstall`) у них ещё и
   рабочий `wingetId` — choco там второй, никогда фактически не проверяемый источник, поэтому
   баг раньше был незаметен. NVIDIA App / AMD Adrenalin — первые записи, где choco остаётся
   единственным источником.

## Решение

### 1. Починить `AvailabilityChecker` (общий фикс, не хак под два пункта)

Добавить проверку через choco как третий шаг цепочки (после winget, после прямой ссылки),
по образцу существующего `GetWingetPackageInfo`:

- Новый приватный метод `GetChocoPackageInfo(string chocoId)`: `choco search <id> --exact -r`
  (машинно-читаемый вывод `-r`, проще парсить, чем текст `GetWingetPackageInfo`).
- Те же гарды, что и у winget-пути: `CommandLineGuard.ValidateId(chocoId)` перед подстановкой,
  respect `_timeoutSeconds` через `CancellationTokenSource`, try/catch → `AppLogger.Write`.
- Вызывается в `CheckAppAvailabilityWithSize`, если `app.ChocoId` не пусто и winget/URL не дали
  `Available`.
- `ParanoidMode`/offline-гейты (строки 79–88) остаются как есть — они уже отсекают choco-путь
  тем же ранним `return`, что и winget/URL.
- Размер пакета через `choco search -r` не сообщается — как и для winget без явного размера,
  используется `DefaultUnknownSizeMB` (100 МБ), это уже существующий паттерн.

### 2. Новые записи в `Catalog/master.json`

Категория «Драйверпаки» (уже существует, рядом с `ddu`/`nvcleanstall`):

```json
{
  "id": "nvidia-app",
  "name": "NVIDIA App",
  "category": "Драйверпаки",
  "wingetId": "",
  "downloadUrl": "",
  "version": "11.0.8.299",
  "size": "",
  "official": true,
  "iconUrl": "https://www.google.com/s2/favicons?domain=nvidia.com&sz=64",
  "description": "Официальное приложение NVIDIA для установки и обновления драйверов видеокарты.",
  "profile": "full",
  "sha256": "",
  "chocoId": "nvidia-app"
},
{
  "id": "amd-adrenalin",
  "name": "AMD Software: Adrenalin Edition",
  "category": "Драйверпаки",
  "wingetId": "",
  "downloadUrl": "",
  "version": "26.6.4",
  "size": "",
  "official": true,
  "iconUrl": "https://www.google.com/s2/favicons?domain=amd.com&sz=64",
  "description": "Официальный пакет драйверов и ПО для видеокарт AMD Radeon.",
  "profile": "full",
  "sha256": "",
  "chocoId": "amd-software-adrenalin-edition"
}
```

`size`/`sha256` пусто — как у остальных choco/winget-only записей без прямой ссылки
(`snappy-driver`, `ddu`). Единственный источник установки — Chocolatey; если он не установлен
у пользователя, `InstallFromChocoAsync` (`InstallationService.Choco.cs`) сам предложит
установить Chocolatey через существующий `confirmPmInstall` — отдельный direct-link фолбэк
не нужен (решение пользователя: только Chocolatey).

### 3. Версия каталога и подпись

- `version`: 11 → 12, новая запись в `changelog` с датой и списком `addedApps`.
- Пересборка `master.json.sig` через `Tools/CatalogSigner` (fail-closed проверка подписи не
  примет каталог без неё).

### 4. Проверка на месте

- Юнит-тест на новый `GetChocoPackageInfo` (мокнуть/изолировать реальный вызов choco по
  аналогии с существующими тестами AvailabilityChecker, если такие есть — иначе хотя бы
  проверить парсинг вывода `choco search -r`).
- Локальный запуск клиента (Debug), проверка что «NVIDIA App» и «AMD Software: Adrenalin
  Edition» в вкладке «Драйверпаки» показывают «✅ Доступно» и чекбокс активен.
- Реальная установка одного из двух (по желанию пользователя на этапе проверки) — подтвердить,
  что `InstallFromChocoAsync` действительно ставит пакет.

## Вне рамок

- Не добавляется определение видеокарты пользователя (AMD/NVIDIA) для скрытия
  нерелевантного пункта — этого в каталоге сейчас нет ни для одной записи, пользователь не
  просил.
- Не добавляется direct-link фолбэк на вендорские страницы.
