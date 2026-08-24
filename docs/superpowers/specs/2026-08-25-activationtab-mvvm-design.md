# ActivationTab — миграция на MVVM (четвёртая вкладка после AboutTab)

## Контекст

Пилот `DebloaterTab`, `HistoryTab`, `AboutTab` уже смержены в `main`. `ActivationTab` — следующий кандидат по возрастанию сложности: 283 строки, один файл, логика — WMI-запрос статуса лицензий Windows/Office (через `SoftwareLicensingProduct` и `OSPP.VBS`), чекбокс согласия, две кнопки-ссылки на сторонний инструмент активации (massgrave.dev) с вспомогательным окном `MasGuideWindow`.

Работа автономная (ночная сессия, без вопросов пользователю по прямому указанию) — решения по дизайну ниже приняты самостоятельно по аналогии с уже смерженными тремя вкладками, без отдельного раунда уточняющих вопросов.

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1, кроме одной механической адаптации: `this.Dispatcher.Invoke(...)` → `System.Windows.Application.Current.Dispatcher.Invoke(...)` (ViewModel не является `DependencyObject`, своего `Dispatcher` нет — стандартная замена с тем же эффектом, тот же приём неявно уже использовался бы, если бы понадобился, в других ViewModel этого проекта).

**Ветка**: `mvvm-activationtab` (создана от `main`), пуш — не делается без отдельного разрешения (та же политика, что у трёх предыдущих вкладок).

## Внешние связи (проверено)

Единственная внешняя точка — `MainWindow.xaml.cs` создаёт `new ActivationTab()`, публичных методов сверх конструктора никто не вызывает. `MasGuideWindow(string product)` — отдельное окно, не трогаем, только вызываем конструктор с тем же аргументом ("Windows"/"Office"), `Owner` выставляется так же. Существующий UI-тест `ActivationTab_ПроверитьСтатус` (`Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs`) кликает `btnCheckStatus` — новый тест не нужен, только регрессия существующего.

## Архитектура

Новый `Ven4Tools/ViewModels/ActivationViewModel.cs`:

| Было (code-behind) | Станет (ViewModel) |
|---|---|
| `chkActivationConsent` + `ChkActivationConsent_Changed` меняет `IsEnabled` двух кнопок | свойство `ConsentGiven` (bool, INPC) — кнопки биндятся на него напрямую через `IsEnabled="{Binding ConsentGiven}"`, отдельный обработчик не нужен |
| `BtnActivateWindows_Click`/`BtnActivateOffice_Click` | `ActivateWindowsCommand`/`ActivateOfficeCommand` (`RelayCommand`, синхронные — `Process.Start` + `new MasGuideWindow(...)`, тот же try/catch) |
| `txtWindowsStatus.Text`/`.Foreground`, `txtOfficeStatus.Text`/`.Foreground` | `WindowsStatusText`/`WindowsStatusBrush`, `OfficeStatusText`/`OfficeStatusBrush` (INPC) |
| `CheckActivationStatusAsync`/`CheckOfficeActivationAsync`/`SetOfficeStatusOnUI`/`CreateLicensingSearcher`/`OfficeCheckTimeout` | те же методы, на ViewModel, тело не меняется кроме `Dispatcher.Invoke` → `Application.Current.Dispatcher.Invoke` |
| `BtnCheckStatus_Click` (`btnCheckStatus.IsEnabled=false` на время проверки) | `CheckStatusCommand` (`RelayCommand.FromAsync`, `CanExecute: _ => !IsCheckingStatus`) — тот же паттерн блокировки на время операции, что `HistoryViewModel.ReinstallCommand`/`IsReinstalling` |
| Публичный `Task CheckActivationStatusAsync()` вызывается из `Loaded` в конструкторе | остаётся публичным на ViewModel, вызывается из `Loaded` в code-behind (WPF-специфичный момент жизненного цикла, не переносится в конструктор VM — тот же принцип, что уже применялся в HistoryTab/AboutTab) |

`OwnerWindowProvider` (`Func<Window?>`) — тот же паттерн, что `DebloaterViewModel`/`CatalogViewModel`, нужен для `MasGuideWindow.Owner`.

## XAML (`ActivationTab.xaml`)

- `chkActivationConsent`: `IsChecked="{Binding ConsentGiven, Mode=TwoWay}"`, обработчики `Checked`/`Unchecked` убираются.
- `btnActivateWindows`/`btnActivateOffice`: `IsEnabled="{Binding ConsentGiven}"` (не `False` статически), `Command="{Binding ActivateWindowsCommand}"`/`ActivateOfficeCommand`.
- `txtWindowsStatus`: `Text="{Binding WindowsStatusText}"`, `Foreground="{Binding WindowsStatusBrush}"`. Аналогично `txtOfficeStatus`.
- `btnCheckStatus`: `Command="{Binding CheckStatusCommand}"`, статический `IsEnabled`-биндинг не нужен — `CanExecute` уже управляет доступностью через встроенный механизм `RelayCommand`+`CommandManager` (тот же вывод, что был подтверждён финальным ревью HistoryTab для аналогичного случая).

## Тестирование (порядок обязателен, как у предыдущих трёх вкладок)

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0 после каждого шага.
2. Юнит-тесты на `ActivationViewModel` — `CreateLicensingSearcher()` (запрос строится верно, чистая функция), `ConsentGiven`/командные `CanExecute` (без реального WMI/Process.Start — те не тестируются, как и раньше не тестировались). `dotnet test` — разрешение на VenchWork уже дано в этой сессии (общее), локально — не запускать без отдельного разрешения (не будет спрошено в эту автономную сессию — юнит-тесты гонять только на VenchWork).
3. Существующий `ActivationTab_ПроверитьСтатус` — прогон на VenchWork (разрешение уже дано).
4. Живой ручной клик — не обязателен (как и для AboutTab), автотестов достаточно для автономного ночного цикла.

## Критерий готовности

- Build 0/0.
- Юнит-тесты новые зелёные.
- `ActivationTab_ПроверитьСтатус` зелёный на VenchWork.
- Слито в `main`, запушено (по установленному в эту сессию правилу — мержить и пушить сразу после верификации, без дополнительного вопроса, т.к. пользователь явно указал не спрашивать и продолжать циклами).
