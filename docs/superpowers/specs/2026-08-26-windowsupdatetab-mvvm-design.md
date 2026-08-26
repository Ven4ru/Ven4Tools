# WindowsUpdateTab — миграция на MVVM (десятая вкладка после SystemTab)

## Контекст

`WindowsUpdateTab` (272 строки, один файл `WindowsUpdateTab.xaml.cs`) — поиск и установка обновлений Windows: дерево категорий с трёхстороннними чекбоксами (`TreeView`, построенный ПРОГРАММНО через `TreeViewItem`/`CheckBox`, полная перерисовка при поиске, частичная синхронизация при клике по патчу), пустое состояние с переходом на «Диагностика», подтверждение EULA в отдельном окне, установка с прогрессом и итоговым окном результата.

Работа автономная, продолжение серии сразу после SystemTab (девять вкладок уже смержены). Ветка `mvvm-windowsupdatetab` от `main`.

## Внешние связи (проверено)

- `MainWindow.xaml.cs:33,230-239` — `_windowsUpdateTab = new WindowsUpdateTab();` (кеш), подписка `_windowsUpdateTab.GoToDiagnostics += () => NavigateToDiagnostics(null, null);` — та же пара «пассивное событие на вкладке + подписка в MainWindow», что и `OfficeTab.GoToActivation`/`DiagnosticsTab.GoToWindowsUpdate`.
- UI-тесты, обязаны сохраниться дословно: `WindowsUpdateTabSmokeTests.cs` — `btnWindowsUpdateTab`→`txtStatus` (только открытие+статус, реальная установка НИКОГДА не вызывается в тестах — небезопасно/непредсказуемо по времени в CI). `Top5FeaturesUiTests.cs` — `btnOpenWindowsUpdate` (это кнопка на ДРУГОЙ вкладке, не трогаем), `btnOpenDiagnostics` (эта кнопка, кросс-навигация).
- `Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs` — существующий сервис с уже готовой tri-state моделью (`WindowsUpdateCategoryNode { Name, Items, bool? IsChecked }`, `WindowsUpdateItemNode { Item, bool IsChecked }`) и чистыми статическими функциями (`Build`/`ApplyCategoryCheck`/`RecalculateCategoryState`/`GetSelectedUpdateIds`/`GetSelectedTotalSizeBytes`/`GetItemsNeedingEula`) — **уже архитектурно похож на VM-модель**, просто без `INotifyPropertyChanged`.
- `tests/Ven4Tools.Tests/WindowsUpdateCategoryTreeBuilderTests.cs` (154 строки) — существующие тесты конструируют `WindowsUpdateCategoryNode`/`WindowsUpdateItemNode` через object-инициализаторы и проверяют результат статических методов. Добавление `INotifyPropertyChanged` — чисто аддитивное изменение (новый интерфейс + событие + notify внутри существующих сеттеров), имена/типы свойств не меняются — эти тесты не должны сломаться.

## Архитектурное решение — единственная НЕ-VM правка в этой миграции

**`WindowsUpdateItemNode`/`WindowsUpdateCategoryNode` (в `Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs`) получают `INotifyPropertyChanged`** — это единственная вкладка серии, где меняется не только `Views/Tabs/*` и `ViewModels/*`, а сам сервисный файл. Обоснование: `TreeView` с `HierarchicalDataTemplate` должен биндиться НА ЭТИ ЖЕ объекты (не на отдельную VM-обёртку) — иначе статические мутирующие функции (`ApplyCategoryCheck`, `RecalculateCategoryState`), которые пишут прямо в `.IsChecked` этих объектов, пришлось бы дублировать или прокидывать через две параллельные иерархии, синхронизируя их. Эти классы уже сегодня — чистая mutable UI-selection-модель без побочных исполнителей (не сериализуются, не используются нигде кроме этой вкладки и её тестов), добавление INPC — минимальная точечная правка, не архитектурное расширение. Существующие статические методы (`Build`/`ApplyCategoryCheck`/`RecalculateCategoryState`/`GetSelectedUpdateIds`/`GetSelectedTotalSizeBytes`/`GetItemsNeedingEula`) остаются БЕЗ ИЗМЕНЕНИЙ — просто их присваивания `.IsChecked = ...` теперь дополнительно поднимают `PropertyChanged`.

Также добавляются два ТОЛЬКО-ДЛЯ-ЧТЕНИЯ вычисляемых свойства для дисплея (без солвания в проверенные тестами методы):
- `WindowsUpdateCategoryNode.HeaderText => $"{Name} ({Items.Count})"` (замена конкатенации, которая раньше жила в `RenderTree()`).
- `WindowsUpdateItemNode.DisplayText => $"{Item.Title}{(KB-часть)} — {SizeFormatter.BytesToMB(Item.SizeBytes)}"`.

## Разбор клика по чекбоксу категории — самая тонкая часть миграции

Оригинал: `CheckBox IsThreeState="True"`, создаётся программно, начальный `IsChecked = category.IsChecked` (включая `null`), обработчик `Click`:
```csharp
categoryCheck.Click += (_, _) => {
    bool newState = categoryCheck.IsChecked == true;  // читается ПОСЛЕ того, как WPF уже перещёлкнул чекбокс
    WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, newState);
    RenderTree();
};
```

Встроенный цикл `ToggleButton` для `IsThreeState="True"`: `true → null → false → true → ...` (из состояния `true` клик уводит в `null`, из `null` — в `false`, из `false` — в `true`). Проверка `categoryCheck.IsChecked == true` ПОСЛЕ этого перехода даёт: клик по `false` → переход в `true` → `newState=true` → выбрать всё; клик по `true` → переход в `null` → `newState=false` → снять всё; клик по `null` → переход в `false` → `newState=false` → снять всё. Итог сводится к чистой функции от состояния ДО клика: **`newState = (текущее IsChecked == false)`** — то есть снятая категория при клике полностью выбирается, любая другая (полностью или частично выбранная) — полностью снимается. Такое поведение для частично выбранной категории (снятие вместо выбора всего) может показаться не интуитивным, но это ТОЧНОЕ поведение оригинала — переносится дословно, без «улучшений».

**Перенос**: чекбокс категории биндится `IsChecked="{Binding IsChecked, Mode=OneWay}"` (НЕ TwoWay — намеренно, не из-за проблемы read-only-краша, а чтобы избежать риска рекурсии/непредсказуемого порядка при попытке закольцевать «клик пишет IsChecked → сеттер каскадирует на детей» непосредственно в свойстве) + `Command="{Binding DataContext.ToggleCategoryCommand, RelativeSource=...}" CommandParameter="{Binding}"`. Обработчик команды вычисляет `newState = category.IsChecked == false` (читает ЕЩЁ НЕ изменённое VM-значение — WPF локально перекинул визуальное состояние чекбокса, но OneWay-биндинг не записал его обратно) и вызывает существующий `WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, newState)` — та же функция, что и в оригинале, без изменений. После выполнения команды `PropertyChanged` от `ApplyCategoryCheck`-присваиваний перетаскивает OneWay-биндинг обратно к корректному значению, перекрывая любое транзиентное локальное состояние, которое успел выставить WPF.

Чекбокс патча (лист дерева, обычный двухсторонний, не tri-state) — **обычный `IsChecked="{Binding IsChecked, Mode=TwoWay}"`** на `WindowsUpdateItemNode.IsChecked` (`bool`, публичный set + INPC) — безопасен, реальный пользовательский ввод, каскада вниз нет (лист).

**Пересчёт состояния категории после клика по патчу** (оригинал: `RecalculateCategoryState(category)` + `UpdateSelectionSummary()` + ручная синхронизация `categoryCheck.IsChecked = category.IsChecked` сразу после клика по патчу, БЕЗ полной перерисовки дерева) — переносится через подписку VM на `PropertyChanged` каждого `WindowsUpdateItemNode.IsChecked` в момент построения дерева (`RunSearchAsync`'а успешная ветка): при каждом изменении вызывается `RecalculateCategoryState(соответствующая category)` (замыкание на `category`, без обратной ссылки item→category в самой модели) + `UpdateSelectionSummary()`. Старые подписки не отписываются явно — дерево целиком заменяется новым набором объектов при каждом поиске (как и в оригинале, который тоже выбрасывает весь `TreeView.Items` и строит заново), старые узлы становятся мусором вместе со своими подписками.

## Дизайн VM (`WindowsUpdateViewModel`, один файл — вкладка меньше SystemTab/DiagnosticsTab, партиалы не нужны)

- `LastCheckedText`/`StatusText` (string, `private set`, дефолты `"Обновления ещё не проверялись"`/`""` — как в XAML).
- `Tree` (`IReadOnlyList<WindowsUpdateCategoryNode>`, `private set`, дефолт пустой массив).
- `ShowEmptyState` (bool, `private set`), `EmptyStateTitle`/`EmptyStateSubtitle` (string, `private set`, дефолты как в XAML), `ShowOpenDiagnosticsButton` (bool, `private set`, дефолт `false` — кнопка `Visibility="Collapsed"` по умолчанию в XAML).
- `SelectionSummaryText` (string, `private set`, дефолт `"Выбрано: 0 патчей, 0 МБ"`).
- `IsInstallEnabled` (bool, `private set`, дефолт `false` — как `btnInstall IsEnabled="False"` в XAML).
- `IsSearching`/`IsInstalling` (bool, `private set`) — оба участвуют в `CheckCommand.CanExecute: !IsSearching && !IsInstalling` (калька `btnCheck.IsEnabled=false` и при поиске, и при установке). `IsInstallEnabled` НЕ входит в `CanExecute` какой-либо команды — остаётся напрямую забинженным bool-свойством на `Button.IsEnabled` (как в оригинале, не через `RelayCommand`), пересчитывается точно в тех местах, где оригинал вызывал `UpdateSelectionSummary()`, плюс явно гасится в момент старта установки (калька `btnInstall.IsEnabled = false;` до пересчёта).
- `OwnerWindowProvider: Func<Window?>?` — для `EulaConfirmWindow`/`WindowsUpdateResultWindow`.
- `event Action? GoToDiagnostics;` — остаётся публичным контрактом самой `WindowsUpdateTab` (как раньше), VM поднимает свой, code-behind ретранслирует — тот же паттерн, что `OfficeTab.GoToActivation`/`DiagnosticsTab.GoToWindowsUpdate`.
- Команды: `CheckCommand` (`FromAsync`, `CanExecute` выше), `InstallCommand` (`FromAsync`, без `CanExecute` — гейт `if (IsInstalling) return;` первой строкой внутри, урок NetworkTab, хотя реальной гонки здесь нет — модальный диалог подтверждения уже сериализует доступ; гейт добавлен для консистентности с остальной серией, не расширяет объём функционально), `ToggleCategoryCommand` (параметризован `WindowsUpdateCategoryNode`), `OpenDiagnosticsCommand` (`_ => GoToDiagnostics?.Invoke()`).
- `_firstRunHandled` — остаётся в code-behind (WPF `Loaded`-lifecycle, тот же принцип, что `_initialized` во всех прошлых вкладках).
- `_searchCts`/`CancellationTokenSource` — переносится во VM как есть (та же cancel-and-restart логика, защита от гонки между авто-проверкой на `Loaded` и мгновенным ручным кликом).

## Порядок `RunSearchAsync` (сохранить дословно)

Отмена предыдущего поиска → `IsSearching=true` → сброс статуса/дерева/пустого состояния → если служба не запущена — диалог (Да/Нет/запуск не удался, во всех трёх «отказных» ветках `IsSearching=false` и выход) → иначе `try`: `SearchAsync` → если отменено (`ct.IsCancellationRequested`) — выйти БЕЗ сброса `IsSearching` (новый поиск сам разберётся) → иначе `IsSearching=false`, обновить `LastCheckedText` → неуспех/пусто/найдено — три ветки с теми же текстами/логами, что в оригинале → `catch (OperationCanceledException) {}` (штатная отмена, не пишем в журнал).

## XAML

`TreeView.ItemsSource="{Binding Tree}"` + `TreeView.Resources` с `HierarchicalDataTemplate DataType="{x:Type wu:WindowsUpdateCategoryNode}" ItemsSource="{Binding Items}"` (заголовок — `CheckBox Content="{Binding HeaderText}" IsThreeState="True" IsChecked="{Binding IsChecked, Mode=OneWay}" Command="{Binding DataContext.ToggleCategoryCommand, RelativeSource={RelativeSource AncestorType=TreeView}} CommandParameter="{Binding}"`) + `DataTemplate DataType="{x:Type wu:WindowsUpdateItemNode}"` (лист — `CheckBox Content="{Binding DisplayText}" IsChecked="{Binding IsChecked, Mode=TwoWay}"`). `xmlns:wu="clr-namespace:Ven4Tools.Services.WindowsUpdate"` — импорт в корень `UserControl` (тот же assembly, `;assembly=` не нужен).

`pnlUpdatesEmpty` → `Visibility` на `ShowEmptyState`; `txtUpdatesEmptyTitle`/`txtUpdatesEmptySubtitle` → `Text` на `EmptyStateTitle`/`EmptyStateSubtitle`; `btnOpenDiagnostics` → `Visibility` на `ShowOpenDiagnosticsButton`, `Command="{Binding OpenDiagnosticsCommand}"`. `btnCheck`/`btnInstall` → `Command`, `btnInstall` дополнительно `IsEnabled="{Binding IsInstallEnabled}"` (OneWay по умолчанию для `Button.IsEnabled`, риска нет). `txtStatus`/`txtLastChecked`/`txtSelectionSummary` → прямые `Text=` биндинги (все `TextBlock`, OneWay по умолчанию, риска нет — в этой вкладке нет ни одного `TextBox`/`ProgressBar`/`Slider`, поэтому класса TwoWay-краша, дважды случившегося в серии, здесь структурно не может быть НИГДЕ, кроме уже разобранного кейса чекбокса категории).

`WindowsUpdateTab.xaml.cs`: конструктор создаёт `WindowsUpdateViewModel`, `DataContext=_viewModel`, `_viewModel.OwnerWindowProvider = () => Window.GetWindow(this);`, ретранслирует `_viewModel.GoToDiagnostics += () => GoToDiagnostics?.Invoke();`, `_firstRunHandled`-гейт в `Loaded` вызывает `await _viewModel.InitializeAsync()`.

## Тестирование

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0.
2. `tests/Ven4Tools.Tests/WindowsUpdateCategoryTreeBuilderTests.cs` — существующие 154 строки тестов должны остаться зелёными без единой правки (INPC — чисто аддитивная правка).
3. Юнит-тесты на `WindowsUpdateViewModel`: дефолты всех свойств, `CanExecute` `CheckCommand` по умолчанию `true`, `ToggleCategoryCommand` — мутационно проверить вычисление `newState = category.IsChecked == false` на всех трёх исходных состояниях (`true`/`false`/`null`), подписка на изменение `WindowsUpdateItemNode.IsChecked` действительно пересчитывает состояние категории и сводку выбора после построения дерева.
4. Живой UI-прогон на VenchWork: `WindowsUpdateTabSmokeTests` (открытие + `txtStatus`), релевантная часть `Top5FeaturesUiTests` (`btnOpenDiagnostics`), `KeyButtonsSmokeTests` как обычно. Реальная установка патчей НЕ вызывается ни в одном тесте (сознательно, см. существующий тест).

## Критерий готовности

- Build 0/0.
- Существующие тесты `WindowsUpdateCategoryTreeBuilderTests` не сломаны.
- Новые юнит-тесты `WindowsUpdateViewModel` зелёные.
- UI-тесты (`WindowsUpdateTabSmokeTests`, релевантная часть `Top5FeaturesUiTests`/`KeyButtonsSmokeTests`) зелёные на VenchWork.
- Финальное цельное ревью ветки — обязательный шаг, с явным указанием перепроверить формулу `newState = category.IsChecked == false` (единственная нетривиальная логика этой миграции) и подписки item→category пересчёта на утечки/повторную подписку при повторных поисках.
- Слито в `main`, запушено — без доп. вопроса. UI-прогон на VenchWork дольше 10-15 минут → эскалация по `feedback_ui_test_hang_escalation`.
