# NetworkTab — миграция на MVVM (пятая вкладка после ActivationTab)

## Контекст

`NetworkTab` (318 строк, один файл) — вкладка сетевой диагностики: список активных адаптеров, пинг 4 хостов, HTTPS-проверка 5 сервисов, определение внешнего IP, DNS-проверка, сброс сетевых настроек (netsh). Логика опирается на существующий `DiagnosticsService` (не трогаем — `AdapterInfo`/`PingResult`/`ServiceCheckResult` уже содержат нужные поля).

Работа автономная (ночная сессия, без вопросов пользователю по прямому указанию) — решения ниже приняты самостоятельно по аналогии с уже смерженными четырьмя вкладками.

**Явное ограничение объёма**: чистый рефакторинг, поведение 1:1, с двумя осознанными механическими адаптациями (обе — уже применённый в предыдущих вкладках паттерн, не новое изобретение):
1. `this.Dispatcher.Invoke(...)` → `System.Windows.Application.Current.Dispatcher.Invoke(...)` (ViewModel не `DependencyObject`).
2. Начальный `Brush` иконок статуса (до первой проверки) — сейчас это неявный `TextPrimary` из глобального `Style TargetType="TextBlock"` в `App.xaml:60` (иконки `txtPingIcon*`/`txtSvc*` не имеют явного `Foreground` в XAML). Явный биндинг на `IconBrush` заменяет этот неявный канал, поэтому дефолт вычисляется тем же способом, что уже проверен ревью `ActivationTab` — `(Application.Current?.TryFindResource("TextPrimary") as Brush) ?? Brushes.White`, без `BrushConverter` (frozen-кисть, потокобезопасна для юнит-тестов).

**Ветка**: `mvvm-networktab` (от `main`), мердж+пуш — сразу после верификации, без доп. вопроса (правило сессии).

## Внешние связи (проверено)

`MainWindow.xaml.cs` создаёт `new NetworkTab()` один раз, кеширует, публичных членов кроме конструктора не вызывает. UI-тесты используют только AutomationId кнопок: `btnNetworkTab`, `btnRunAll`, `btnRefreshAdapters`, `btnPing`, `btnCheckServices`, `btnGetIp`, `btnCheckDns` (в `Phase3RemainingTabsTests.NetworkTab_ОстальныеДиагностическиеКнопки` и `KeyButtonsSmokeTests`) — `btnResetNetwork` НИГДЕ не кликается тестами (реально меняет сеть). Ни один тест не обращается к `txtPing*`/`txtSvc*`/`txtPublicIp`/`txtDnsResult`/`lstAdapters` по AutomationId — их можно перепривязать на биндинги без риска для тестового контракта, но `x:Name` не убираем (дешевле сохранить на будущее, не мешает).

## Архитектура

Новый `Ven4Tools/ViewModels/NetworkViewModel.cs`, плюс вспомогательный `internal sealed class NetworkCheckResult : INotifyPropertyChanged` (три биндуемых свойства: `Text`, `IconText`, `IconBrush`) — заменяет пары `(TextBlock ms, TextBlock icon)`, которыми оперировали `SetPingRow`/инлайн-лямбда в `RunServicesAsync`. Один тип обслуживает и 4 строки пинга, и 5 строк сервисов — они обновляются идентичной логикой (`ok:bool? → текст/иконка/цвет`), сейчас размазанной по двум местам; в VM это один приватный статический `SetRow(NetworkCheckResult, string text, bool? ok)`, повторяющий тело `SetPingRow` — чистая консолидация дублирования, не новая функциональность.

| Было (code-behind) | Станет (ViewModel) |
|---|---|
| `_busy` (bool поле) | `IsBusy` (bool, INPC) — блокирует ВСЕ команды на время `RunAllAsync`, как `SetDiagnosticButtonsEnabled(false)` |
| `btnPing`/`btnCheckServices`/`btnGetIp`/`btnCheckDns` индивидуальные `IsEnabled` | `IsPinging`/`IsCheckingServices`/`IsGettingIp`/`IsCheckingDns` (bool, INPC) — `CanExecute` соответствующей команды = `!IsBusy && !свойство` |
| `btnResetNetwork.IsEnabled` | `IsResettingNetwork` (bool, INPC) — `CanExecute` = `!IsBusy && !IsResettingNetwork` |
| `RunAllAsync`/`RefreshAdapters`/`RunPingAsync`/`RunServicesAsync`/`RunGetIpAsync`/`RunDnsAsync`/`RunResetNetworkAsync` | те же методы на VM, тела без изменений кроме адаптации Dispatcher и замены `TextBlock`-присваиваний на свойства |
| `btnRunAll.Content = "⏳ Диагностика..." / "🔍 Запустить..."` | `RunAllButtonText` (string, INPC), `Button.Content="{Binding RunAllButtonText}"` |
| `lstAdapters.ItemsSource` / `txtAdaptersEmpty.Visibility` | `Adapters` (`IReadOnlyList<AdapterInfo>`, INPC) / `AdaptersEmpty` (bool, INPC) → `BooleanToVisibilityConverter` (уже используется в `AboutTab.xaml`/`InstalledTab.xaml` — тот же `x:Key="BoolToVis"`) |
| `txtPublicIp.Text` | `PublicIpText` (string, INPC), дефолт `"не определён"` (как в XAML сейчас) |
| `txtDnsResult.Text`/`.Visibility` | `DnsResultText` (string, INPC) / `DnsResultVisible` (bool, INPC, дефолт `false`) |

**Важная деталь семантики `IsBusy` vs individual-флаги** (сохранить дословно — иначе поведение разойдётся с оригиналом): в оригинале `SetDiagnosticButtonsEnabled(true)` в `finally` блоке `RunAllAsync` **безусловно** возвращает все 7 кнопок в `IsEnabled=true`, независимо от того, что каждый индивидуальный метод внутри цикла (`RunPingAsync` и др.) НЕ включил их сам обратно (условие `if (!_busy) btn.IsEnabled = true` было ложным, пока `_busy==true`). Эквивалент в VM: `RunAllAsync`'s `finally` обязан явно сбросить **все** индивидуальные флаги (`IsPinging = IsCheckingServices = IsGettingIp = IsCheckingDns = false`) в дополнение к `IsBusy = false` — иначе флаги останутся `true` навсегда и соответствующие команды не смогут выполниться повторно после `RunAll`. `IsResettingNetwork` в этот сброс не входит — сброс сети не вызывается из `RunAllAsync` в оригинале, как и сейчас.

Команды (все `RelayCommand`/`RelayCommand.FromAsync`, тот же паттерн, что `ActivationViewModel`):
- `RunAllCommand` (`FromAsync`, `CanExecute: _ => !IsBusy`)
- `RefreshAdaptersCommand` (синхронный `RelayCommand`, `CanExecute: _ => !IsBusy`)
- `PingCommand` (`FromAsync`, `CanExecute: _ => !IsBusy && !IsPinging`)
- `CheckServicesCommand` (`FromAsync`, `CanExecute: _ => !IsBusy && !IsCheckingServices`)
- `GetIpCommand` (`FromAsync`, `CanExecute: _ => !IsBusy && !IsGettingIp`)
- `CheckDnsCommand` (`FromAsync`, `CanExecute: _ => !IsBusy && !IsCheckingDns`)
- `ResetNetworkCommand` (`FromAsync`, `CanExecute: _ => !IsBusy && !IsResettingNetwork`)

`MessageBox.Show`/`Process.Start`/`ProfileService.Current.ParanoidMode`/`AppLogger.Write`/`TrustedExecutablePaths.CmdExe` вызываются из VM напрямую (тот же прагматичный подход, что уже принят и одобрен ревью в `ActivationViewModel` для `Process.Start`/WMI — без абстракций ради тестируемости, раз оригинал и так не тестировал эту логику).

## Тестируемая чистая функция (по аналогии с `CreateLicensingSearcher` у Activation)

`internal static void SetRow(NetworkCheckResult row, string text, bool? ok)` — чистая, не трогает UI напрямую (работает над `NetworkCheckResult`, не над `TextBlock`), детерминированная. Юнит-тесты проверяют все 3 ветки (`ok=null` → "⬜"/Gray; `ok=true` → "✅"/зелёный; `ok=false` → "❌"/светло-коралловый) и что `Text` присваивается как есть.

## XAML (`NetworkTab.xaml`)

- `<UserControl.Resources><BooleanToVisibilityConverter x:Key="BoolToVis"/></UserControl.Resources>` (как в `AboutTab.xaml`).
- `btnRunAll`: `Content="{Binding RunAllButtonText}"`, `Command="{Binding RunAllCommand}"`.
- `lstAdapters`: `ItemsSource="{Binding Adapters}"` (шаблон элемента не меняется — `AdapterInfo.Name/Type/Ip` уже те поля, что и сейчас).
- `txtAdaptersEmpty`: `Visibility="{Binding AdaptersEmpty, Converter={StaticResource BoolToVis}}"`.
- `btnRefreshAdapters`: `Command="{Binding RefreshAdaptersCommand}"`.
- `txtPing1`..`txtPing4`: `Text="{Binding Ping1.Text}"`..`{Binding Ping4.Text}`.
- `txtPingIcon1`..`txtPingIcon4`: `Text="{Binding Ping1.IconText}"`, `Foreground="{Binding Ping1.IconBrush}"` (аналогично Ping2-4).
- `btnPing`: `Command="{Binding PingCommand}"`.
- `txtSvc1`..`txtSvc5`: `Text="{Binding Svc1.IconText}"`, `Foreground="{Binding Svc1.IconBrush}"` (аналогично Svc2-5).
- `txtSvcMs1`..`txtSvcMs5`: `Text="{Binding Svc1.Text}"` (аналогично).
- `btnCheckServices`: `Command="{Binding CheckServicesCommand}"`.
- `txtPublicIp`: `Text="{Binding PublicIpText}"`.
- `btnGetIp`: `Command="{Binding GetIpCommand}"`.
- `btnCheckDns`: `Command="{Binding CheckDnsCommand}"`.
- `txtDnsResult`: `Text="{Binding DnsResultText}"`, `Visibility="{Binding DnsResultVisible, Converter={StaticResource BoolToVis}}"`.
- `btnResetNetwork`: `Command="{Binding ResetNetworkCommand}"`.
- Статический `IsEnabled` ни на одной кнопке не нужен — `CanExecute` + `CommandManager` управляют доступностью (подтверждённый предыдущими вкладками вывод).

`NetworkTab.xaml.cs`: конструктор создаёт `NetworkViewModel`, `DataContext = _viewModel`, `Loaded += (_, _) => _viewModel.RefreshAdaptersCommand.Execute(null);` (в оригинале `Loaded += (_, _) => RefreshAdapters();` — вызываем через команду, чтобы CanExecute-гейт применялся одинаково везде). `OwnerWindowProvider` не нужен — эта VM не открывает окна.

## Тестирование (порядок обязателен, как у предыдущих четырёх вкладок)

1. `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental` — 0/0 после каждого шага.
2. Юнит-тесты на `NetworkViewModel`: `SetRow` (все 3 ветки × корректность Text/IconText/IconBrush), дефолтные значения свойств при конструировании (`RunAllButtonText`, `PublicIpText`, `DnsResultVisible=false`, `AdaptersEmpty` до первого обновления, `IconBrush` дефолт = белый фолбэк без `Application`), `CanExecute` каждой команды в паре состояний `IsBusy`/индивидуальный флаг. WMI/HTTP/Process/MessageBox не тестируем (как и раньше не тестировались — сервис-слой `DiagnosticsService` не меняется). `dotnet test` — только на VenchWork (общее разрешение сессии), локально не гонять без отдельного разрешения.
3. Существующий UI-тест `NetworkTab_ОстальныеДиагностическиеКнопки` (`Phase3RemainingTabsTests.cs`) + `KeyButtonsSmokeTests` (уже ссылается на `btnNetworkTab`/`btnRunAll`) — прогон на VenchWork.
4. Живой ручной клик — не обязателен (автономная ночная сессия, как и для предыдущих вкладок).

## Критерий готовности

- Build 0/0.
- Юнит-тесты новые зелёные.
- `NetworkTab_ОстальныеДиагностическиеКнопки` и `KeyButtonsSmokeTests` зелёные на VenchWork.
- Финальное цельное ревью ветки (после обоих task-ревью) — обязательный шаг перед мерджем, как и в предыдущих 4 вкладках; там трижды подряд находились межзадачные пробелы, которые точечные ревью структурно не видят.
- Слито в `main`, запушено — без доп. вопроса (правило сессии).
