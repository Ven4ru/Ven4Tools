# OfficeTab — миграция на MVVM (шестая вкладка после NetworkTab)

## Контекст

`OfficeTab` (686 строк, 4 partial-файла code-behind: `OfficeTab.xaml.cs` — ядро/конструктор, `OfficeTab.Download.cs`, `OfficeTab.Install.cs`, `OfficeTab.Region.cs`) — самая рискованная вкладка из мигрированных на сегодня: реальное скачивание установщика Office с серверов Microsoft, проверка Authenticode-подписи, запуск установщика с повышением прав (`runas`), временная подмена региона Windows/Office через реестр (с persistent-маркером восстановления на диске на случай hard-kill), координация с общим `InstallationService.InstallSemaphore`/`UiGuards.WarnIfInstallBusy()` (тот же семафор, что каталог и History), и публичное событие `GoToActivation`, на которое подписывается `MainWindow`.

Работа автономная (ночная сессия, без вопросов пользователю) — решения ниже приняты самостоятельно по аналогии с пятью уже смерженными вкладками.

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1, с четырьмя явными механическими адаптациями (все — уже применённый в этой серии паттерн):
1. `this.Dispatcher.Invoke(...)` → `System.Windows.Application.Current.Dispatcher.Invoke(...)`.
2. `Views.UiGuards.WarnIfInstallBusy()` / `InstallationService.InstallSemaphore` вызываются из VM напрямую — уже устоявшийся паттерн в этом кодовом месте (`HistoryViewModel`, `CatalogViewModel.Install.cs`, `AppCardViewModel` уже делают ровно это), никакой новой абстракции не нужно.
3. Публичное событие `event Action? GoToActivation` остаётся на самом `OfficeTab` (UserControl) — `MainWindow.xaml.cs:250` подписывается на него напрямую (`_officeTab.GoToActivation += ...`), это внешний контракт, который нельзя переносить. VM получает свой собственный `event Action? GoToActivation`, поднимаемый из `GoActivationCommand`; code-behind в конструкторе перепроброcывает `_viewModel.GoToActivation += () => GoToActivation?.Invoke();` — тот же паттерн ретрансляции события, что уже обсуждался (see `DiagnosticsTab`/`WindowsUpdateTab` комментарии, ссылающиеся на этот же приём у OfficeTab).
4. Достижимые из конструктора VM обращения к WPF-хосту защищены от `Application.Current == null`: в `UpdateRegionDisplay()` используется `Application.Current?.Dispatcher.Invoke(...)` (тот же `?.`-паттерн, что в `ActivationViewModel`/`CatalogViewModel`), а вызов `RecoverRegionFromBackup()` в конструкторе обусловлен `if (Application.Current != null)` — иначе `dotnet test` падал бы с `NullReferenceException` и, что важнее, реально перезаписывал бы `HKCU`-регион на машине разработчика, чего не было раньше, когда этот вызов жил в конструкторе `OfficeTab` (в реальном приложении `Application.Current` никогда не `null`, поведение не меняется).

**Ветка**: `mvvm-officetab` (от `main`), мердж+пуш — сразу после верификации, без доп. вопроса (правило сессии).

## Внешние связи (проверено)

- `MainWindow.xaml.cs:247-252` — единственная точка создания (`new OfficeTab()`, кешируется в `_officeTab`), плюс подписка на `GoToActivation`.
- UI-тесты: `KeyButtonsSmokeTests.cs` (`GoTo("btnOfficeTab", "btnDownloadOffice", "Office")` — только навигация, реальный клик по скачиванию не делается), `Phase3RemainingTabsTests.OfficeTab_ОтменаИПереходКАктивации` — проверяет **`btnCancelOffice.IsEnabled == false`** вне активной операции (критично: `CancelCommand.CanExecute` должен по умолчанию быть `false`, а не `true`) и что клик по `btnGoActivation` реально переводит на ActivationTab (`btnCheckStatus` находится после перехода). `AuditFixesUiTests.cs` явно документирует, что реальная установка Office не тестируется живьём (слишком рискованно/долго) — только diff/build.
- Все `x:Name`, участвующие в тестах, сохраняются дословно: `btnOfficeTab` (кнопка вкладки в MainWindow, не эта вкладка), `btnDownloadOffice`, `btnCancelOffice`, `btnGoActivation`.

## Архитектура

Новый `Ven4Tools/ViewModels/OfficeViewModel.cs` (ядро) + partial-файлы `OfficeViewModel.Download.cs`/`OfficeViewModel.Install.cs`/`OfficeViewModel.Region.cs` — та же файловая структура, что у code-behind, и уже устоявшийся в проекте паттерн partial VM (`CatalogViewModel.Install.cs`, `CatalogViewModel.Presets.cs`).

### Выбор версии (5 RadioButton → 5 bool-свойств)

`IsO365Selected`/`IsO2024Selected`/`IsO2021Selected`/`IsO2019Selected`/`IsO2016Selected` (bool, INPC, TwoWay-биндинг на `IsChecked` каждой кнопки). `GroupName="OfficeVersion"` в `Style` остаётся как есть — встроенный механизм WPF для RadioButton с общим `GroupName` сам сбрасывает `IsChecked` остальных при выборе новой (это снимет `IsChecked=false` через TwoWay-биндинг на соответствующее свойство автоматически, отдельная синхронизация не нужна). Дефолт: `IsO365Selected = true` (как `IsChecked="True"` у `rdbO365` в оригинале), инициализируется через field initializer (не через сеттер) — чтобы не вызвать спонтанно логику инвалидации скачанного файла при конструировании (см. ниже).

`GetSelectedVersion()` → чистая функция `internal static (string DisplayName, string ProductId) ResolveVersion(bool o2024, bool o2021, bool o2019, bool o2016)` (порядок проверки как в оригинале: 2024 → 2021 → 2019 → 2016 → иначе 365), тестируема без UI.

### Язык интерфейса

`OfficeLanguages` (`string[]`, тот же массив из 8 языков, публичное readonly-свойство для `ItemsSource`), `SelectedLanguage` (string, INPC, TwoWay), дефолт `officeLanguages[0]` = `"ru-ru"` (эквивалент `SelectedIndex=0` после `FillComboBoxes()`).

### Инвалидация скачанного установщика при смене версии/языка (M2)

Оригинал подписывается на `.Checked`/`.SelectionChanged` ПОСЛЕ `FillComboBoxes()`, чтобы начальные значения не считались «сменой». В VM это воспроизводится тем, что сама логика инвалидации (`OnSelectionChanged()`) вызывается ИЗ СЕТТЕРА свойства, а не из внешней подписки — начальные значения задаются field initializer'ами (минуя сеттер), поэтому конструктор не может случайно вызвать инвалидацию. Дополнительное условие: у радио-свойств инвалидация вызывается только когда сеттер получает `true` (эквивалент подписки именно на `Checked`, не на `Unchecked` — оригинал не слушает `Unchecked` вообще); у `SelectedLanguage` — при любом изменении значения (эквивалент `SelectionChanged`, который стреляет в любую сторону).

### Прогресс / статус

`ProgressVisible` (bool, default `false`), `InstallPhaseText` (string, default `"⏳ Подготовка..."` — да, оригинал держит этот текст в свёрнутой панели с самого начала, даже когда `pnlProgress` ещё `Collapsed`; сохраняем 1:1), `ProgressValue` (double, default `0`), `InstallDetailText` (string, default `""`), `ProgressIndeterminate` (bool, default `false`, отражает `progressOffice.IsIndeterminate`, переключается вокруг `MonitorInstallation`).

### Кнопки и busy-состояние

| Оригинал | VM |
|---|---|
| `btnDownloadOffice.IsEnabled` (false во время скачивания ИЛИ установки) | `DownloadCommand.CanExecute: _ => !IsDownloading && !IsInstalling` |
| `btnInstallOffice.IsEnabled` (false по умолчанию, true после успешного скачивания, false при смене версии/языка, false в начале установки, в `finally` установки — `_downloadedFilePath != null && File.Exists(...)`) | `HasDownloadedInstaller` (bool, INPC) — выставляется ИМПЕРАТИВНО в тех же точках, что оригинал выставлял `IsEnabled` (включая финальную проверку `File.Exists` в `finally`, а не наивное «оставить как было»); `InstallCommand.CanExecute: _ => HasDownloadedInstaller && !IsDownloading && !IsInstalling` |
| `btnCancelOffice.IsEnabled`/`.Visibility` | `CancelEnabled` (bool, default `false` — совпадает с `IsEnabled="False"` в XAML и напрямую проверяется существующим UI-тестом `OfficeTab_ОтменаИПереходКАктивации`) и `CancelVisible` (bool, default `true` — в оригинальном XAML `Visibility` у `btnCancelOffice` не задан явно, что означает `Visible` по умолчанию; код лишь временно прячет её после запуска elevated-установщика и снова показывает в начале каждой операции) |

`CancelCommand` выполняет то же, что инлайн-лямбда в оригинале (`_cancellationTokenSource?.Cancel(); CancelEnabled=false; AppLogger.Write(...)`), `CanExecute: _ => CancelEnabled`.

Гейт реентерабельности (урок NetworkTab, см. память `project_ven4tools_mvvm_migration_networktab_2026_08_25`): `DownloadCommand`/`InstallCommand`/`CancelCommand` асинхронны через `CanExecute`+`CommandManager` (тот же асинхронный Background-priority зазор) — по аналогии с уроком NetworkTab, `RunDownloadAsync`/`RunInstallAsync` начинаются с явного раннего выхода (`if (IsDownloading) return;`/`if (IsInstalling) return;`) первой строкой, а не полагаются только на `CanExecute`.

### Регион

`RegionGeoText`/`RegionCCText` (string, INPC, default `"—"`) — заменяют `txtRegionGeo.Text`/`txtRegionCC.Text`. `UpdateRegionDisplay`/`SaveRegion`/`SetRegionUS`/`RestoreRegion`/`RecoverRegionFromBackup` переносятся в `OfficeViewModel.Region.cs` без изменения тела (кроме адаптации `Dispatcher.Invoke`). `OfficeRegionRecoveryService`/`Registry.CurrentUser` — сервис-слой и системный реестр, не абстрагируются (тот же прагматичный подход, что WMI в `ActivationViewModel`).

### Установка

`BtnInstallOffice_Click` → `RunInstallAsync()`, тело без изменений кроме: `Dispatcher.Invoke`-адаптации, `SetPhase`/`SetProgress`/`SetDetail` теперь VM-методы, `AuthenticodeVerifier.IsSignedByMicrosoft`/`Process.Start`/`MessageBox.Show`/registry-вызовы — напрямую из VM (тот же прагматизм, что уже принят ревью в `ActivationViewModel`/`NetworkViewModel`). `GetC2RProcessPids`/`WaitForC2RProcess`/`MonitorInstallation` — переносятся как есть (статические/приватные helper-методы).

### Скачивание

`BtnDownloadOffice_Click` → `RunDownloadAsync()`, тело без изменений кроме адаптаций выше. `officeDirectLinks`/`CreateHttpClient()`/`_httpClient` (статическое поле) — переносятся как есть в ядро VM.

### Активация

`btnGoActivation.Click` → `GoActivationCommand` (`RelayCommand`, синхронный, поднимает `GoToActivation?.Invoke()`), `CanExecute` не нужен (кнопка всегда доступна, как в оригинале — там нет никакого `IsEnabled` управления для неё вообще).

`pnlActivationHint.Visibility` — оригинал ставит `Visible` безусловно в конструкторе поверх XAML-дефолта `Collapsed`; в VM это упрощается до `ActivationHintVisible = true` как единственное значение по умолчанию (устраняет мёртвую пару XAML-default/ctor-override, поведение идентично — панель видна сразу и всегда, как и в оригинале).

## XAML (`OfficeTab.xaml`)

- 5 `RadioButton` — `IsChecked="{Binding IsOXxxSelected, Mode=TwoWay}"` вместо статического `IsChecked="True"` у `rdbO365`.
- `cmbOfficeLanguage`: `ItemsSource="{Binding OfficeLanguages}"`, `SelectedItem="{Binding SelectedLanguage, Mode=TwoWay}"`.
- `chkSaveInstaller`: `IsChecked="{Binding SaveInstaller, Mode=TwoWay}"` вместо статического `IsChecked="False"`.
- `btnDownloadOffice`/`btnInstallOffice`/`btnCancelOffice`/`btnGoActivation`: `Command="{Binding ...Command}"`, статический `IsEnabled` убирается везде, КРОМЕ того что `CanExecute` естественным образом даёт те же дефолты (Install/Cancel — `false`, Download — `true`).
- `btnCancelOffice`: дополнительно `Visibility="{Binding CancelVisible, Converter={StaticResource BoolToVis}}"` (конвертер — `UserControl.Resources`, тот же `x:Key="BoolToVis"` паттерн, что в `AboutTab`/`InstalledTab`/`NetworkTab`).
- `pnlProgress`: `Visibility="{Binding ProgressVisible, Converter={StaticResource BoolToVis}}"`.
- `txtInstallPhase`: `Text="{Binding InstallPhaseText}"`.
- `progressOffice`: `Value="{Binding ProgressValue}"`, `IsIndeterminate="{Binding ProgressIndeterminate}"`.
- `txtInstallDetail`: `Text="{Binding InstallDetailText}"`.
- `txtRegionGeo`/`txtRegionCC`: `Text="{Binding RegionGeoText}"`/`{Binding RegionCCText}`.
- `pnlActivationHint`: `Visibility="{Binding ActivationHintVisible, Converter={StaticResource BoolToVis}}"` вместо статического `Collapsed`.

`OfficeTab.xaml.cs`: конструктор создаёт `OfficeViewModel`, `DataContext = _viewModel`, ретранслирует `_viewModel.GoToActivation += () => GoToActivation?.Invoke();`, публичное `event Action? GoToActivation;` остаётся на самом `OfficeTab`. `OwnerWindowProvider` не нужен (VM не открывает окна, только `MessageBox.Show` без owner — как в оригинале).

## Тестирование (порядок обязателен, как у предыдущих пяти вкладок)

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0 после каждого шага.
2. Юнит-тесты на `OfficeViewModel`: `ResolveVersion` (все 5 комбинаций/приоритетов), дефолты всех свойств при конструировании, `CanExecute` каждой команды в состоянии по умолчанию (Download=true, Install=false, Cancel=false — **последнее прямо требуется существующим UI-тестом**, обязательно проверить юнит-тестом тоже), `InstallCommand.CanExecute` становится `true` после `HasDownloadedInstaller=true` (через `internal set`), `GoActivationCommand` поднимает `GoToActivation`. **Не тестировать** инвалидацию при смене версии/языка (`OnVersionOrLanguageChanged`) — она вызывает `SetProgress`, которая обращается к `Application.Current.Dispatcher`; в юнит-хосте `Application.Current == null`, и тест обязан НЕ доходить до этого кода. Guard `OnVersionOrLanguageChanged` обязан проверять именно приватное поле `_downloadedFilePath == null` (как в оригинале), а не публичное свойство `HasDownloadedInstaller` — тогда любой тест, меняющий `IsOXxxSelected`/`SelectedLanguage` на свежесозданной VM (файл ещё не скачан), безопасно проходит guard-выход, не доходя до `Application.Current`. Сетевые вызовы/реестр/elevated-процессы не тестируем (как и раньше не тестировались). `dotnet test` — только на VenchWork.
3. Существующий `OfficeTab_ОтменаИПереходКАктивации` (`Phase3RemainingTabsTests.cs`) + `KeyButtonsSmokeTests` (навигация на Office) — прогон на VenchWork.
4. Живой ручной клик — не обязателен (автономная ночная сессия). Реальное скачивание/установку Office НЕ запускать даже вручную — слишком рискованно/долго для верификации рефакторинга (та же позиция, что уже зафиксирована в `AuditFixesUiTests.cs` для существующих тестов).

## Критерий готовности

- Build 0/0.
- Юнит-тесты новые зелёные, включая обязательную проверку `CancelCommand.CanExecute(null) == false` по умолчанию.
- `OfficeTab_ОтменаИПереходКАктивации` и `KeyButtonsSmokeTests` (навигация Office) зелёные на VenchWork.
- Финальное цельное ревью ветки — обязательный шаг перед мерджем; в предыдущих 4 вкладках подряд находило реальные межзадачные пробелы.
- Слито в `main`, запушено — без доп. вопроса.
