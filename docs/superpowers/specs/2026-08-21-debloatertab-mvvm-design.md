# DebloaterTab — миграция на MVVM (пилот перед SystemTab и остальными вкладками)

## Контекст

Клиент Ven4Tools мигрировал на MVVM только вкладку «Каталог» (`CatalogTab`, 2026-07-13). Остальные 9 вкладок (`InstalledTab`, `SystemTab`, `OfficeTab`, `ActivationTab`, `DebloaterTab`, `NetworkTab`, `HistoryTab`, `AboutTab`, `DiagnosticsTab`) — обычные `UserControl` с логикой в code-behind (partial-классы).

Пользователь выбрал полную MVVM-миграцию всех вкладок как «глобальный апдейт» — следующий мажорный релиз (5.6.0 по зафиксированной схеме версионирования, см. память `project_ven4tools_versioning_scheme`). Задача разбита на независимые подпроекты по вкладке — этот документ описывает первый, пилотный: **`DebloaterTab`**.

`DebloaterTab` выбран пилотом за низкий риск: 191 строка, один файл, логика системных операций (Appx/реестр/службы/PowerShell) уже вынесена в `DebloatTweakExecutor`/`DebloatCatalog` (round 35, 2026-08-10) — в code-behind осталась почти голая UI-обвязка.

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1. Никаких попутных исправлений находок — они фиксируются отдельно, не в этом заходе.

**Явное ограничение по репозиторию**: работа ведётся в локальной ветке `mvvm-full-migration` (создана от `main`), коммиты только локальные, **пуш в `origin` не делается** до отдельного явного разрешения — сначала нужен обильный прогон тестов.

## Существующая связка, которую нельзя сломать

`Ven4Tools/Views/Tabs/SystemTab.Snapshots.cs` напрямую обращается к `DebloaterTab` как к View (не через ViewModel): `MainWindow.EnsureDebloaterTab()` возвращает инстанс, `SystemTab.Snapshots.cs` зовёт на нём `GetSelectedTweakIds()`, `SetSelectedTweakIds(ids)`, `ApplyTweaksByIdsAsync(ids, progress, ct)` — это часть механизма снапшотов конфигурации (`ConfigSnapshotService`).

**Требование**: публичная сигнатура этих трёх методов на `DebloaterTab` должна остаться идентичной. `SystemTab.Snapshots.cs` и `MainWindow.xaml.cs` — **не трогать**.

## Архитектура

Новый файл `Ven4Tools/ViewModels/DebloaterViewModel.cs` — переносит из `DebloaterTab.xaml.cs`:

| Было (code-behind) | Станет (ViewModel) |
|---|---|
| `_allItems` (поле) | `_allItems` (поле ViewModel) |
| `GetFilteredItems()`/`ApplyFilter()` (dispatch в `lstDebloat.ItemsSource`) | свойство `FilteredItems` с `SetField`/`OnPropertyChanged`, пересчитывается при смене `CategoryFilter` |
| `BtnSelectAll_Click`/`BtnSelectNone_Click` | `SelectAllCommand`/`SelectNoneCommand` (`RelayCommand`) |
| `BtnApplyDebloat_Click` (async, try/finally, диалог подтверждения+точка восстановления, прогресс, отмена) | `ApplyCommand` (`RelayCommand.FromAsync`), внутренние поля прогресса/статуса как bindable-свойства |
| `BtnCancelDebloat_Click` | `CancelCommand` |
| `GetSelectedTweakIds()`/`SetSelectedTweakIds()`/`ApplyTweaksByIdsAsync()` (публичные, используются `SystemTab.Snapshots.cs`) | те же методы, публичные, на ViewModel |
| `_cts` (CancellationTokenSource для отмены) | то же поле на ViewModel |

`DebloaterTab.xaml.cs` становится тонкой обёрткой (по образцу `CatalogTab.xaml.cs`):
- `private readonly DebloaterViewModel _viewModel = new();`
- `DataContext = _viewModel;` + `_viewModel.OwnerWindowProvider = () => Window.GetWindow(this);` в конструкторе.
- Три публичных метода (`GetSelectedTweakIds`/`SetSelectedTweakIds`/`ApplyTweaksByIdsAsync`) — однострочные форварды на `_viewModel`.

`DebloatItem.cs` — организационный перенос `Views/Tabs/DebloatItem.cs` → `ViewModels/DebloatItem.cs` (по аналогии с `AppRowViewModel`), namespace `Ven4Tools.Views.Tabs` → `Ven4Tools.ViewModels`. Само тело класса не меняется. Требует обновить `using` в файлах, где тип упоминается (`DebloaterViewModel.cs`, `SystemTab.Snapshots.cs` если ссылается на тип напрямую — проверить при реализации).

## XAML (`DebloaterTab.xaml`)

- `<ItemsControl x:Name="lstDebloat">` → добавить `ItemsSource="{Binding FilteredItems}"`, `x:Name` можно оставить (не мешает биндингу) либо убрать, если больше нигде не используется из code-behind.
- Четыре кнопки (`btnDebloatSelectAll`, `btnDebloatSelectNone`, `btnApplyDebloat`, `btnCancelDebloat`) — `Click="..."` заменяется на `Command="{Binding ...Command}"`.
- Четыре `RadioButton` (`rbAll`/`rbApps`/`rbPrivacy`/`rbServices`) — `Checked="FilterChanged"` остаётся как обработчик в code-behind, тело — один вызов `_viewModel.CategoryFilter = "..."` (не полноценный биндинг, тот же стиль, что `CatalogTab.xaml.cs` использует для part своей логики жизненного цикла — не всё обязано быть чистым `Binding`).
- `progressDebloat`/`txtDebloatStatus` — `Value`/`Visibility`/`Text` переводятся на биндинги к свойствам ViewModel (`ProgressValue`, `ProgressVisible`, `StatusText`).

## Диалоги

`ApplyCommand` вызывает `UiGuards.ConfirmAndCreateRestorePointAsync(...)` — требует окно-владельца. Решается через `OwnerWindowProvider`, тот же паттерн, что `CatalogViewModel.OwnerWindowProvider`.

## Тестирование (порядок обязателен)

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0 после каждого значимого шага.
2. `dotnet test tests/Ven4Tools.Tests` — юнит-тесты, только с явного разрешения пользователя на запуск (см. память `feedback_no_tests_without_agreement`).
3. Существующий `DebloaterTab_ВыбратьВсеИСброс` (`Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs`) — кликает по кнопкам через `Invoke()`, не трогает чекбоксы напрямую через `IsChecked=` — риска гонки `CommandManager.RequerySuggested` (найдена в round 40 на тесте пресетов) здесь не предвидится, но перепроверить после миграции живым прогоном, не полагаться на теорию.
4. Полный `Ven4Tools.ClientUITests` — на ICL по обычному рецепту (`schtasks /it /rl HIGHEST`), только с явного разрешения.
5. `btnApplyDebloat`/реальное применение твиков — **не кликать** в автотестах (реально удаляет Appx-пакеты/трогает реестр и службы), как и в существующем тесте — только код-ревью сигнатур и путей вызова.
6. Живой ручной клик по вкладке «Очистка» на домашнем ПК (фильтры, «Все»/«Сброс», прогресс/статус визуально) — обязателен перед тем, как считать пилот завершённым, поскольку `dotnet build`/юнит-тесты не ловят WPF-специфичные рантайм-сбои (см. правило в `agent_context.md` §7 «WPF-заметки»).

## Критерий готовности пилота

- Build 0/0.
- `DebloaterTab_ВыбратьВсеИСброс` зелёный (изолированно и в полном наборе).
- Живой клик подтверждает: фильтры переключаются, «Все»/«Сброс» работают в рамках текущего фильтра (не всех 35 твиков сразу — это уже исправленный баг, вести себя так же), `SystemTab` → «Снимки» → сохранение/восстановление снапшота видит те же твики, что выбраны на «Очистке» (сквозная проверка связки, которую мы обязаны не сломать).
- Всё это — в локальной ветке `mvvm-full-migration`, не запушено.
