# HistoryTab — миграция на MVVM (вторая вкладка после пилота DebloaterTab)

## Контекст

Пилот `DebloaterTab` (2026-08-21) подтвердил процесс MVVM-миграции клиента Ven4Tools в рамках глобального апдейта 5.6.0 (см. `project_ven4tools_versioning_scheme`). `HistoryTab` выбран следующей вкладкой как самая простая из оставшихся девяти: 192 строки, один файл, никакой внешней вкладки не тянет за собой напрямую (в отличие от `DebloaterTab`, который держал связку с `SystemTab.Snapshots.cs`).

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1, с одним осознанным отступлением (см. ниже). Никаких попутных фич.

**Явное ограничение по репозиторию**: работа в локальной ветке `mvvm-full-migration` (та же, что у пилота), коммиты только локальные, пуш в `origin` — не делается без отдельного явного разрешения.

## Внешние связи (проверено)

Единственный внешний вызов — `MainWindow.NavigateToHistory` создаёт `new HistoryTab()` и зовёт `RefreshAsync()`. Публичная сигнатура этого метода должна остаться неизменной. Больше никто (`PinsStripController`, `UiGuards`) не обращается к `HistoryTab` напрямую — упоминания там текстовые (комментарии про уже примененный фикс `SilentArgs`), не вызовы.

## Архитектура

Новый `Ven4Tools/ViewModels/HistoryViewModel.cs`, переносит из `HistoryTab.xaml.cs`:

| Было (code-behind) | Станет (ViewModel) |
|---|---|
| `_allEntries` (поле) | `_allEntries` (поле ViewModel) |
| `ApplyFilter()` → `lstHistory.ItemsSource` | свойство `FilteredEntries`, пересчитывается при смене `SearchText`/`SuccessOnly`/`FailOnly` |
| `txtHistoryCount.Text = list.Count` | свойство `HistoryCount` (string) |
| `chkSaveHistory.IsChecked` + `ChkSaveHistory_Click` | свойство `SaveHistory` (get/set делегирует `ProfileService.Current.SaveInstallHistory` + `Save()` + `AppLogger.Write`, как сейчас) |
| `BtnClearHistory_Click` (диалог + `InstallHistoryService.ClearAsync()`) | `ClearHistoryCommand` (`RelayCommand.FromAsync`) |
| `BtnReinstall_Click` (async, семафор, try/catch) | `ReinstallCommand` (`RelayCommand.FromAsync`, параметр — `HistoryEntry`) |
| `RefreshAsync()` (публичный) | тот же метод, публичный, на ViewModel |

`HistoryEntry` (`Ven4Tools/Models/HistoryEntry.cs`) уже лежит в `Models/` — переносить не требуется (в отличие от `DebloatItem` в пилоте).

## Что остаётся в code-behind

WPF-специфичные, не MVVM-концепции (тот же принцип, что в пилоте — не всё обязано быть чистым `Binding`):

- `Loaded`/`Unloaded` — подписка/отписка `InstallHistoryService.Instance.Changed` (привязано к жизненному циклу `UserControl`, вкладка кэшируется в `MainWindow` и переиспользуется).
- Placeholder-логика `txtHistorySearch` (`GotFocus`/`LostFocus` меняют текст на подсказку и обратно).
- `TxtHistorySearch_TextChanged` — тонкий форвард: если текст не плейсхолдер, `_viewModel.SearchText = txtHistorySearch.Text.Trim();` иначе `_viewModel.SearchText = "";`.

## XAML (`HistoryTab.xaml`)

- `chkSaveHistory`: `IsChecked="{Binding SaveHistory, Mode=TwoWay}"`, `Click` убирается.
- `togSuccessOnly`/`togFailOnly`: прямой двусторонний `IsChecked="{Binding SuccessOnly}"`/`{Binding FailOnly}` — гонки `InitializeComponent`, как у `RadioButton` в пилоте, здесь нет (в разметке нет `IsChecked="True"` по умолчанию).
- `lstHistory`: `ItemsSource="{Binding FilteredEntries}"`.
- `txtHistoryCount`: `Text="{Binding HistoryCount}"`.
- `btnClearHistory`: `Command="{Binding ClearHistoryCommand}"`, `Click` убирается.
- Кнопка «🔄» в `DataTemplate`: `Command="{Binding DataContext.ReinstallCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"`, `CommandParameter="{Binding}"` — паттерн, уже используемый в `CatalogTab.xaml` (`ToggleFavoriteCommand`, `ApplyPresetCommand` и др.), `Click` и ручной `Tag="{Binding}"` убираются.

## Осознанное отступление от 1:1 — `IsReinstalling`

Сейчас клик «Переустановить» блокирует только нажатую кнопку (`btn.IsEnabled = false` в `finally`). С командным биндингом без обёртки каждой строки в собственную ViewModel это не воспроизвести буквально. Решение: `ReinstallCommand.CanExecute` проверяет глобальный флаг `IsReinstalling` на ViewModel — блокирует **все** кнопки «Переустановить», пока идёт одна. `InstallSemaphore` и так сериализует установки — по факту это чуть строже старого поведения, а не слабее. Согласовано с пользователем.

## Уточнение: try/catch в `ReinstallAsync` — переносится, не убирается

Первая версия этого документа предлагала убрать try/catch `BtnReinstall_Click`, полагаясь на общий перехват `RelayCommand.FromAsync`. При ближайшем рассмотрении это меняет поведение: `FromAsync` гасит `OperationCanceledException` молча и логирует прочие ошибки общей фразой «Ошибка выполнения команды» — а текущий код пишет прицельные `⏹️ Переустановка {имя} прервана` / `❌ Ошибка переустановки {имя}: {сообщение}`. Это видимый пользователю текст в логе, терять его — не 1:1. Решение: try/catch переезжает в `ReinstallAsync` как есть (тот же текст), `RelayCommand.FromAsync` остаётся внешней страховкой (для этого метода на практике не сработает — все исключения уже перехвачены внутри, но не мешает).

## Тестирование (порядок обязателен, по образцу пилота)

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0 после каждого значимого шага.
2. `dotnet test tests/Ven4Tools.Tests` — только с явного разрешения пользователя на запуск (`feedback_no_tests_without_agreement`).
3. Новый UI-регресс-тест `HistoryTab_ПоискФильтрОчистка` (`Ven4Tools.ClientUITests`, по образцу `DebloaterTab_ВыбратьВсеИСброс`) — покрывает поиск, оба фильтра, очистку истории. Кнопку «Переустановить» реально не кликать в автотесте (реальная установка/сеть) — как и `btnApplyDebloat` в пилоте, только код-ревью сигнатуры и пути вызова.
4. Полный `Ven4Tools.ClientUITests` — на **VenchWork** (`100.93.198.62`, замена ICL) по рецепту `schtasks /it /rl HIGHEST`, только с явного разрешения.
5. Живой ручной клик по вкладке «История» на домашнем ПК — поиск, оба фильтра, очистка, переустановка одного реального приложения — обязателен перед тем, как считать задачу завершённой (билд/юнит не ловят WPF-специфичные рантайм-сбои, см. баги пилота: гонка `InitializeComponent`, `OneWay`-биндинг).

## Критерий готовности

- Build 0/0.
- `HistoryTab_ПоискФильтрОчистка` зелёный (изолированно и в полном наборе).
- Живой клик подтверждает: поиск и оба фильтра фильтруют список, «Очистить» очищает после подтверждения, «Переустановить» ставит приложение и корректно логирует успех/ошибку/отмену, `IsReinstalling` блокирует остальные кнопки на время операции.
- Всё это — в локальной ветке `mvvm-full-migration`, не запушено.
