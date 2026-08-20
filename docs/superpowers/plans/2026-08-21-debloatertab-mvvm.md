# DebloaterTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перевести вкладку «Очистка» (`DebloaterTab`) с code-behind на MVVM: вся логика (фильтр, выбор, применение твиков) переезжает в новый `DebloaterViewModel`, `DebloaterTab.xaml.cs` становится тонкой обёрткой, поведение не меняется.

**Architecture:** Тот же паттерн, что `CatalogTab`/`CatalogViewModel` (2026-07-13): `UserControl` создаёт `ViewModel` в конструкторе, ставит его в `DataContext`, XAML биндится к свойствам/командам ViewModel. Публичный контракт `DebloaterTab` (три метода, которые дёргает `SystemTab.Snapshots.cs`) сохраняется байт-в-байт — методы становятся однострочными форвардами на ViewModel.

**Tech Stack:** C# / .NET 8 / WPF, `Ven4Tools.ViewModels.RelayCommand` (уже есть в проекте), xUnit для тестов.

## Global Constraints

- Спек: `docs/superpowers/specs/2026-08-21-debloatertab-mvvm-design.md` — читать перед началом.
- Чистый рефакторинг, поведение 1:1. Никаких попутных фиксов, даже мелких.
- `SystemTab.Snapshots.cs` и `MainWindow.xaml.cs` — **не трогать**, сигнатура `DebloaterTab.GetSelectedTweakIds()`/`SetSelectedTweakIds()`/`ApplyTweaksByIdsAsync()` не меняется.
- Работа в локальной ветке `mvvm-full-migration` (уже создана и активна). Коммитить локально после каждой задачи. **Не пушить** в `origin` без отдельного явного разрешения.
- `dotnet test` (запуск юнит-тестов) — только с явного разрешения пользователя каждый раз (см. память `feedback_no_tests_without_agreement`). `dotnet build` — можно свободно.
- Все тексты (комментарии, сообщения) — только на русском.

---

### Task 1: Перенести `DebloatItem` из `Views/Tabs` в `ViewModels`

**Files:**
- Move: `Ven4Tools/Views/Tabs/DebloatItem.cs` → `Ven4Tools/ViewModels/DebloatItem.cs`
- Modify: `Ven4Tools/Views/Tabs/DebloatCatalog.cs`

**Interfaces:**
- Produces: `Ven4Tools.ViewModels.DebloatItem` (класс, публичный конструктор `DebloatItem(string name, string id, string category, string risk, string description)`, свойства `Name`/`Id`/`Category`/`Risk`/`Description`/`RiskLabel`/`IsSelected`, реализует `INotifyPropertyChanged`) — используется в Task 2 и Task 3.

- [ ] **Step 1: Переместить файл и поменять namespace**

Новое содержимое `Ven4Tools/ViewModels/DebloatItem.cs` (полностью, единственное изменение — `namespace`):

```csharp
using System.ComponentModel;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка списка на вкладке «Очистка»: описание твика плюс состояние галочки.
    /// Вынесено из code-behind вкладки — это модель строки, а не логика окна.
    /// </summary>
    public class DebloatItem : INotifyPropertyChanged
    {
        public string Name        { get; }
        public string Id          { get; }
        public string Category    { get; } // "app", "privacy", "service"
        public string Risk        { get; } // "safe", "moderate", "caution"
        public string Description { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string RiskLabel => Risk switch
        {
            "safe"     => "Безопасно",
            "moderate" => "Умеренно",
            "caution"  => "Осторожно",
            _          => Risk
        };

        public DebloatItem(string name, string id, string category, string risk, string description)
        {
            Name = name; Id = id; Category = category; Risk = risk; Description = description;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
```

Удалить старый файл `Ven4Tools/Views/Tabs/DebloatItem.cs`.

- [ ] **Step 2: Добавить `using` в `DebloatCatalog.cs`**

`Ven4Tools/Views/Tabs/DebloatCatalog.cs` — добавить строку `using Ven4Tools.ViewModels;` после существующего `using System.Collections.Generic;` (строка 1). Остальное содержимое файла (сам список 35 твиков) не трогать.

Итог верхней части файла:

```csharp
using System.Collections.Generic;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок (в `DebloaterTab.xaml.cs` пока будут ошибки — `DebloatItem` не найден в старом namespace без `using`, это ожидаемо и чинится в Task 3). Если ошибок ТОЛЬКО в `DebloaterTab.xaml.cs` — двигаться дальше, это нормально на этом шаге.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/ViewModels/DebloatItem.cs Ven4Tools/Views/Tabs/DebloatCatalog.cs
git rm Ven4Tools/Views/Tabs/DebloatItem.cs
git commit -m "refactor(debloater): DebloatItem переезжает в ViewModels"
```

---

### Task 2: Создать `DebloaterViewModel` + регрессионные юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/DebloaterViewModel.cs`
- Create: `tests/Ven4Tools.Tests/DebloaterViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.DebloatItem` (Task 1), `Ven4Tools.Views.Tabs.DebloatCatalog.BuildItems()` (существующий), `Ven4Tools.Services.DebloatTweakExecutor.ApplyItemAsync(string category, string id, string displayName, CancellationToken ct = default)` (существующий), `Ven4Tools.Services.AppLogger.Write(string)` (существующий), `Ven4Tools.Views.UiGuards.ConfirmAndCreateRestorePointAsync(string question, string restoreDescription, Action<string>? log = null)` и `Ven4Tools.Views.RestorePointOutcome` (существующие), `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync` (существующий).
- Produces: `Ven4Tools.ViewModels.DebloaterViewModel` — публичные члены: `Func<Window?>? OwnerWindowProvider`, `string CategoryFilter` (get/set), `List<DebloatItem> FilteredItems` (get), `double ProgressValue` (get), `Visibility ProgressVisible` (get), `string StatusText` (get), `bool ApplyEnabled` (get), `Visibility CancelVisible` (get), `bool CancelEnabled` (get), `RelayCommand SelectAllCommand`, `RelayCommand SelectNoneCommand`, `RelayCommand ApplyCommand`, `RelayCommand CancelCommand`, `IReadOnlyList<string> GetSelectedTweakIds()`, `void SetSelectedTweakIds(IReadOnlyCollection<string> ids)`, `Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(IReadOnlyCollection<string> ids, IProgress<string>? progress = null, CancellationToken ct = default)`. Используется в Task 3.

- [ ] **Step 1: Написать `DebloaterViewModel.cs`**

Полное содержимое `Ven4Tools/ViewModels/DebloaterViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «Очистка» — только UI-состояние: фильтр по категориям, отметки,
    /// прогресс и кнопки. Список твиков живёт в <see cref="DebloatCatalog"/>, а сами
    /// системные операции (Appx, реестр, службы, PowerShell) — в
    /// <see cref="DebloatTweakExecutor"/>. Перенесено из code-behind при переходе
    /// на MVVM (2026-08-21, пилот перед остальными вкладками), поведение не менялось.
    /// </summary>
    public sealed class DebloaterViewModel : INotifyPropertyChanged
    {
        private readonly List<DebloatItem> _allItems = DebloatCatalog.BuildItems();
        private CancellationTokenSource? _cts;

        public Func<Window?>? OwnerWindowProvider { get; set; }

        private string _categoryFilter = "all";
        public string CategoryFilter
        {
            get => _categoryFilter;
            set { if (SetField(ref _categoryFilter, value)) ApplyFilter(); }
        }

        private List<DebloatItem> _filteredItems = new();
        public List<DebloatItem> FilteredItems
        {
            get => _filteredItems;
            private set => SetField(ref _filteredItems, value);
        }

        private double _progressValue;
        public double ProgressValue { get => _progressValue; private set => SetField(ref _progressValue, value); }

        private Visibility _progressVisible = Visibility.Collapsed;
        public Visibility ProgressVisible { get => _progressVisible; private set => SetField(ref _progressVisible, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

        private bool _applyEnabled = true;
        public bool ApplyEnabled { get => _applyEnabled; private set => SetField(ref _applyEnabled, value); }

        private Visibility _cancelVisible = Visibility.Collapsed;
        public Visibility CancelVisible { get => _cancelVisible; private set => SetField(ref _cancelVisible, value); }

        private bool _cancelEnabled = true;
        public bool CancelEnabled { get => _cancelEnabled; private set => SetField(ref _cancelEnabled, value); }

        public RelayCommand SelectAllCommand { get; }
        public RelayCommand SelectNoneCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand CancelCommand { get; }

        public DebloaterViewModel()
        {
            SelectAllCommand = new RelayCommand(_ => SelectAll());
            SelectNoneCommand = new RelayCommand(_ => SelectNone());
            ApplyCommand = RelayCommand.FromAsync(async _ => await ApplyAsync());
            CancelCommand = new RelayCommand(_ => Cancel());
            ApplyFilter();
        }

        // ── Фильтр/выбор ─────────────────────────────────────────────────────────

        /// <summary>
        /// Элементы, показанные текущим фильтром. Вынесено отдельно, потому что этот
        /// же набор нужен «Все»: она обязана отмечать ровно то, что пользователь
        /// видит на экране, а не весь список целиком.
        /// </summary>
        private List<DebloatItem> GetFilteredItems() =>
            CategoryFilter == "all"
                ? _allItems.ToList()
                : _allItems.Where(i => i.Category == CategoryFilter).ToList();

        private void ApplyFilter() => FilteredItems = GetFilteredItems();

        // Отмечаем только видимые сейчас действия. Раньше отмечались все 35 сразу:
        // пользователь, выбрав фильтр «Приложения» и нажав «Все», молча ставил галки
        // ещё и на правки реестра и на отключение служб (DiagTrack, SysMain,
        // dmwappushservice), которых на экране не было — и «Применить» их выполняло.
        private void SelectAll()
        {
            foreach (var item in GetFilteredItems()) item.IsSelected = true;
            ApplyFilter();
        }

        private void SelectNone()
        {
            foreach (var item in _allItems) item.IsSelected = false;
            ApplyFilter();
        }

        // ── Публичный доступ для снапшотов конфигурации ─────────────────────────

        /// <summary>Идентификаторы отмеченных сейчас твиков (для сохранения в снапшот).</summary>
        public IReadOnlyList<string> GetSelectedTweakIds() =>
            _allItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();

        /// <summary>Отмечает в UI ровно те твики, чьи идентификаторы переданы.</summary>
        public void SetSelectedTweakIds(IReadOnlyCollection<string> ids)
        {
            foreach (var item in _allItems)
                item.IsSelected = ids.Contains(item.Id);
            ApplyFilter();
        }

        /// <summary>
        /// Применяет твики по идентификаторам тем же путём, что и обычная «Применить»
        /// (удаление Appx, реестр, службы). Используется восстановлением снапшота
        /// конфигурации. Неизвестные идентификаторы пропускаются.
        /// </summary>
        public async Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(
            IReadOnlyCollection<string> ids,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var items = _allItems.Where(i => ids.Contains(i.Id)).ToList();
            int succeeded = 0;
            foreach (var item in items)
            {
                progress?.Report(item.Name);
                bool ok = await DebloatTweakExecutor.ApplyItemAsync(item.Category, item.Id, item.Name, ct);
                AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name} (из снапшота)");
                if (ok) succeeded++;
            }
            return (succeeded, items.Count);
        }

        // ── Apply ────────────────────────────────────────────────────────────────

        private async Task ApplyAsync()
        {
            var selected = _allItems.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано.", "Debloater",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyEnabled = false;
            ProgressVisible = Visibility.Visible;
            ProgressValue = 0;

            // try/finally: любое исключение в процессе (зависший PowerShell, сбой
            // сервиса и т.п.) не должно оставить кнопку и прогресс-бар навсегда
            // заблокированными — состояние восстанавливается в любом случае.
            try
            {
                var hasRisky = selected.Any(i => i.Risk is "caution" or "moderate");
                var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                    $"Будет применено {selected.Count} действий.{(hasRisky ? "\n\n⚠️ Среди них есть умеренные/опасные операции." : "")}\n\nСоздать точку восстановления Windows перед очисткой?",
                    "Ven4Tools — перед очисткой системы");
                if (rpOutcome == Views.RestorePointOutcome.Cancelled)
                {
                    StatusText = "Отменено";
                    ProgressVisible = Visibility.Collapsed;
                    return;
                }

                _cts = new CancellationTokenSource();
                CancelVisible = Visibility.Visible;
                CancelEnabled = true;
                int done = 0;
                int succeeded = 0;

                foreach (var item in selected)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    StatusText = $"⚙️ {item.Name}...";
                    ProgressValue = (double)done / selected.Count * 100;

                    bool ok = await DebloatTweakExecutor.ApplyItemAsync(item.Category, item.Id, item.Name, _cts.Token);
                    AppLogger.Write($"{(ok ? "✅" : "❌")} {item.Name}");
                    if (ok) succeeded++;
                    done++;
                }

                if (_cts.Token.IsCancellationRequested)
                {
                    StatusText = $"⏹ Остановлено: применено {succeeded} из {selected.Count}";
                }
                else
                {
                    ProgressValue = 100;
                    StatusText = $"✅ Готово: применено {succeeded} из {selected.Count}";
                }
            }
            finally
            {
                ApplyEnabled = true;
                CancelVisible = Visibility.Collapsed;
                _cts?.Dispose(); _cts = null;
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            CancelEnabled = false;
            StatusText = "⏹ Останавливаю...";
        }

        // ── INotifyPropertyChanged ───────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
```

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок (включая `DebloaterTab.xaml.cs` — он пока не переписан и продолжит использовать старый `DebloatCatalog`/`_allItems` напрямую; убедиться, что ошибки, если есть, относятся ТОЛЬКО к `DebloaterTab.xaml.cs`, не к новому файлу).

- [ ] **Step 3: Написать регрессионные юнит-тесты**

Полное содержимое `tests/Ven4Tools.Tests/DebloaterViewModelTests.cs`:

```csharp
using System.Linq;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «Очистка», перенесённая из code-behind в ViewModel
/// (2026-08-21). Реальные системные операции (DebloatTweakExecutor) здесь не
/// проверяются — только фильтр/выбор/снапшот-контракт, всё чистые методы.
/// </summary>
public class DebloaterViewModelTests
{
    [Fact]
    public void CategoryFilter_ПоУмолчанию_ПоказываетВсеКатегории()
    {
        var vm = new DebloaterViewModel();

        var distinctCategories = vm.FilteredItems.Select(i => i.Category).Distinct().ToList();

        Assert.True(distinctCategories.Count >= 2,
            "По умолчанию (фильтр 'all') должны быть видны твики нескольких категорий, не одной.");
    }

    [Fact]
    public void CategoryFilter_ApplyFiltruet_ТолькоСвоюКатегорию()
    {
        var vm = new DebloaterViewModel();

        vm.CategoryFilter = "app";

        Assert.All(vm.FilteredItems, item => Assert.Equal("app", item.Category));
        Assert.True(vm.FilteredItems.Count > 0, "В каталоге должен быть хотя бы один твик категории app.");
    }

    [Fact]
    public void SelectAllCommand_ОтмечаетТолькоВидимыеФильтром()
    {
        var vm = new DebloaterViewModel();
        vm.CategoryFilter = "app";

        vm.SelectAllCommand.Execute(null);
        var selectedIds = vm.GetSelectedTweakIds();

        // Ни одного твика из других категорий быть не должно — это тот самый баг,
        // который был исправлен до MVVM-переезда (см. комментарий в SelectAll()).
        // Проверяем через полный список (фильтр "all"), а не через побочный эффект
        // внутри Select — так понятнее, что именно проверяется.
        vm.CategoryFilter = "all";
        var otherCategorySelected = vm.FilteredItems
            .Where(i => selectedIds.Contains(i.Id) && i.Category != "app")
            .ToList();

        Assert.True(selectedIds.Count > 0);
        Assert.Empty(otherCategorySelected);
    }

    [Fact]
    public void SelectNoneCommand_СнимаетВсеОтметкиНезависимоОтФильтра()
    {
        var vm = new DebloaterViewModel();
        vm.CategoryFilter = "all";
        vm.SelectAllCommand.Execute(null);
        Assert.NotEmpty(vm.GetSelectedTweakIds());

        vm.SelectNoneCommand.Execute(null);

        Assert.Empty(vm.GetSelectedTweakIds());
    }

    [Fact]
    public void SetSelectedTweakIds_ВосстанавливаетРовноПереданныеИдентификаторы()
    {
        var vm = new DebloaterViewModel();
        var allIds = vm.FilteredItems.Select(i => i.Id).ToList();
        var subset = allIds.Take(2).ToList();

        vm.SetSelectedTweakIds(subset);

        var selected = vm.GetSelectedTweakIds();
        Assert.Equal(subset.OrderBy(x => x), selected.OrderBy(x => x));
    }

    [Fact]
    public void SetSelectedTweakIds_ИгнорируетНеизвестныеИдентификаторы()
    {
        var vm = new DebloaterViewModel();

        vm.SetSelectedTweakIds(new[] { "не-существующий-id-12345" });

        Assert.Empty(vm.GetSelectedTweakIds());
    }
}
```

- [ ] **Step 4: Запустить тесты (с разрешения пользователя)**

Спросить пользователя явно: «Можно запустить `dotnet test tests/Ven4Tools.Tests --filter DebloaterViewModelTests`?» Только после «да»:

Run: `dotnet test tests/Ven4Tools.Tests --filter FullyQualifiedName~DebloaterViewModelTests`
Expected: все 6 тестов из Step 3 зелёные.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/ViewModels/DebloaterViewModel.cs tests/Ven4Tools.Tests/DebloaterViewModelTests.cs
git commit -m "feat(debloater): DebloaterViewModel + регрессионные юнит-тесты"
```

---

### Task 3: Переписать `DebloaterTab.xaml`/`DebloaterTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/DebloaterTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/DebloaterTab.xaml.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.DebloaterViewModel` (Task 2) — все публичные члены, перечисленные в Task 2.
- Produces: `DebloaterTab` с публичным контрактом, идентичным исходному: `GetSelectedTweakIds()`, `SetSelectedTweakIds(ids)`, `ApplyTweaksByIdsAsync(ids, progress, ct)` — используется `SystemTab.Snapshots.cs` (не меняется).

- [ ] **Step 1: Переписать `DebloaterTab.xaml`**

Полное содержимое `Ven4Tools/Views/Tabs/DebloaterTab.xaml` (изменения — только атрибуты `ItemsSource`/`Command`/`Value`/`Visibility`/`IsEnabled`/`Text`, отмечены комментариями `<!-- MVVM -->` для наглядности при ревью, сами комментарии в файл не добавлять):

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.DebloaterTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Margin="0,0,0,16">
            <StackPanel>
                <TextBlock Text="🧹 Очистка Windows"
                           FontSize="22" FontWeight="Bold"
                           Foreground="{DynamicResource TextPrimary}"/>
                <TextBlock TextWrapping="Wrap" Margin="0,4,0,0"
                           Foreground="{DynamicResource TextSecondary}" FontSize="12">
                    Удаление предустановленного мусора и настройка конфиденциальности.
                    Все действия обратимы через «Обновление Windows» или реестр.
                </TextBlock>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Top">
                <Button x:Name="btnDebloatSelectAll" Content="Все" Width="50" Height="30"
                        ToolTip="Отметит все действия очистки, показанные текущим фильтром."
                        Margin="0,0,6,0" Command="{Binding SelectAllCommand}"/>
                <Button x:Name="btnDebloatSelectNone" Content="Сброс" Width="60" Height="30"
                        ToolTip="Снимет отметки со всех действий очистки."
                        Command="{Binding SelectNoneCommand}"/>
            </StackPanel>
        </Grid>

        <!-- Filter tabs -->
        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,12">
            <RadioButton x:Name="rbAll"      Content="Все"               GroupName="DebloatFilter"
                         IsChecked="True"    Margin="0,0,14,0"
                         Foreground="{DynamicResource TextPrimary}"
                         Checked="FilterChanged"/>
            <RadioButton x:Name="rbApps"     Content="📦 Приложения"    GroupName="DebloatFilter"
                         Margin="0,0,14,0"
                         Foreground="{DynamicResource TextPrimary}"
                         Checked="FilterChanged"/>
            <RadioButton x:Name="rbPrivacy"  Content="🔒 Конфиденциальность" GroupName="DebloatFilter"
                         Margin="0,0,14,0"
                         Foreground="{DynamicResource TextPrimary}"
                         Checked="FilterChanged"/>
            <RadioButton x:Name="rbServices" Content="⚙️ Службы"       GroupName="DebloatFilter"
                         Foreground="{DynamicResource TextPrimary}"
                         Checked="FilterChanged"/>
        </StackPanel>

        <!-- Items list -->
        <Border Grid.Row="2" Background="{DynamicResource CardBackground}"
                CornerRadius="10" Padding="4">
            <ScrollViewer VerticalScrollBarVisibility="Auto">
                <ItemsControl x:Name="lstDebloat" ItemsSource="{Binding FilteredItems}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Border Padding="10,7" Margin="2,1"
                                    CornerRadius="7">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Background" Value="Transparent"/>
                                        <Style.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter Property="Background"
                                                        Value="{DynamicResource BorderBrush}"/>
                                            </Trigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <CheckBox Grid.Column="0" IsChecked="{Binding IsSelected, Mode=TwoWay}"
                                              VerticalAlignment="Center" Margin="0,0,10,0"/>
                                    <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                        <TextBlock Text="{Binding Name}" FontSize="13" FontWeight="Medium"
                                                   Foreground="{DynamicResource TextPrimary}"/>
                                        <TextBlock Text="{Binding Description}" FontSize="11"
                                                   Foreground="{DynamicResource TextSecondary}"
                                                   TextWrapping="Wrap"/>
                                    </StackPanel>
                                    <Border Grid.Column="2" CornerRadius="5" Padding="7,3"
                                            VerticalAlignment="Center" Margin="8,0,0,0">
                                        <Border.Style>
                                            <Style TargetType="Border">
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding Risk}" Value="safe">
                                                        <Setter Property="Background" Value="#1B5E20"/>
                                                    </DataTrigger>
                                                    <DataTrigger Binding="{Binding Risk}" Value="moderate">
                                                        <Setter Property="Background" Value="#E65100"/>
                                                    </DataTrigger>
                                                    <DataTrigger Binding="{Binding Risk}" Value="caution">
                                                        <Setter Property="Background" Value="#B71C1C"/>
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </Border.Style>
                                        <TextBlock Text="{Binding RiskLabel}" FontSize="10" Foreground="White"/>
                                    </Border>
                                </Grid>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>
        </Border>

        <!-- Action bar -->
        <Grid Grid.Row="3" Margin="0,12,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel>
                <ProgressBar x:Name="progressDebloat" Height="5" Minimum="0" Maximum="100"
                             Foreground="{DynamicResource AccentColor}"
                             Background="{DynamicResource BorderBrush}"
                             Value="{Binding ProgressValue}"
                             Visibility="{Binding ProgressVisible}" Margin="0,0,0,5"/>
                <TextBlock x:Name="txtDebloatStatus" FontSize="11"
                           Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap"
                           Text="{Binding StatusText}"/>
            </StackPanel>
            <Button x:Name="btnCancelDebloat" Grid.Column="1"
                    Content="⏹ Отмена"
                    ToolTip="Остановит применение оставшихся действий. Уже выполненные изменения сохранятся."
                    Height="38" Padding="16,0" FontWeight="SemiBold"
                    Visibility="{Binding CancelVisible}"
                    IsEnabled="{Binding CancelEnabled}"
                    Command="{Binding CancelCommand}" Margin="12,0,0,0"/>
            <Button x:Name="btnApplyDebloat" Grid.Column="2"
                    Content="🧹 Применить выбранное"
                    ToolTip="После подтверждения создаст точку восстановления и применит выбранные изменения Windows."
                    Height="38" Padding="16,0" FontWeight="SemiBold"
                    Background="{StaticResource StatusDanger}" Foreground="#06130D"
                    IsEnabled="{Binding ApplyEnabled}"
                    Command="{Binding ApplyCommand}" Margin="12,0,0,0"/>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `DebloaterTab.xaml.cs`**

Полное содержимое `Ven4Tools/Views/Tabs/DebloaterTab.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Очистка» — тонкая обёртка над <see cref="DebloaterViewModel"/>.
    /// Вся логика перенесена в ViewModel при переходе на MVVM (2026-08-21, пилот
    /// перед остальными вкладками). Публичный контракт (три метода ниже) сохранён
    /// без изменений — <c>SystemTab.Snapshots.cs</c> обращается к ним напрямую.
    /// </summary>
    public partial class DebloaterTab : UserControl
    {
        private readonly DebloaterViewModel _viewModel = new();

        public DebloaterTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);
        }

        // Единственная логика, оставшаяся в code-behind: RadioButton.Checked не
        // биндится напрямую (GroupName делает их взаимоисключающими через сам WPF,
        // а не через ViewModel), поэтому читаем состояние трёх именованных элементов,
        // как делал исходный GetFilteredItems().
        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            if (rbApps.IsChecked == true) _viewModel.CategoryFilter = "app";
            else if (rbPrivacy.IsChecked == true) _viewModel.CategoryFilter = "privacy";
            else if (rbServices.IsChecked == true) _viewModel.CategoryFilter = "service";
            else _viewModel.CategoryFilter = "all";
        }

        // ── Публичный доступ для снапшотов конфигурации (SystemTab.Snapshots.cs) ──

        public IReadOnlyList<string> GetSelectedTweakIds() => _viewModel.GetSelectedTweakIds();

        public void SetSelectedTweakIds(IReadOnlyCollection<string> ids) => _viewModel.SetSelectedTweakIds(ids);

        public Task<(int Succeeded, int Total)> ApplyTweaksByIdsAsync(
            IReadOnlyCollection<string> ids,
            IProgress<string>? progress = null,
            CancellationToken ct = default) =>
            _viewModel.ApplyTweaksByIdsAsync(ids, progress, ct);
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests` (там ссылок на внутренности `DebloaterTab` быть не должно, только на `AutomationId`, но проверить это сборкой, не предположением).

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/Tabs/DebloaterTab.xaml Ven4Tools/Views/Tabs/DebloaterTab.xaml.cs
git commit -m "refactor(debloater): DebloaterTab — тонкая обёртка над DebloaterViewModel"
```

---

### Task 4: Верификация — регрессия и живой клик

**Files:**
- Не создаёт и не меняет файлы (только проверка того, что сделано в Task 1-3).

**Interfaces:**
- Не применимо (верификационная задача).

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Спросить разрешение и прогнать юнит-тесты целиком**

Спросить: «Можно прогнать весь `dotnet test tests/Ven4Tools.Tests`?» После «да»:

Run: `dotnet test tests/Ven4Tools.Tests`
Expected: было 372/372 до этой задачи (см. память `audit_2026_08_20_round40`) + 6 новых из `DebloaterViewModelTests` = 378/378. Если число other — разбираться, не игнорировать расхождение.

- [ ] **Step 3: Спросить разрешение и прогнать существующий UI-тест изолированно**

Спросить: «Можно прогнать `DebloaterTab_ВыбратьВсеИСброс` живым запуском клиента (домашний ПК или ICL)?» После «да», по обычному рецепту (домашний ПК — напрямую при свободном рабочем столе; ICL — `schtasks /it /rl HIGHEST`, см. память `reference_ui_tests_known_issues_20260724`):

Run: `dotnet test Ven4Tools.ClientUITests --filter FullyQualifiedName~DebloaterTab_ВыбратьВсеИСброс`
Expected: 1/1 пройден.

- [ ] **Step 4: Живой ручной клик (обязателен, не пропускать)**

Запустить клиент (`dotnet run --project Ven4Tools` либо уже собранный `_release`), открыть вкладку «Очистка»:
1. Переключить все 4 фильтра («Все»/«Приложения»/«Конфиденциальность»/«Службы») — список должен меняться, ничего не отмечается само.
2. На фильтре «Приложения» нажать «Все» → отметиться должны ТОЛЬКО видимые (категория app), переключить на «Все» (фильтр) → отметки на других категориях отсутствуют.
3. Нажать «Сброс» → все отметки снимаются независимо от фильтра.
4. Отметить 1-2 безопасных твика, **не нажимать «Применить»** (реально меняет систему) — визуально убедиться, что кнопка доступна, статус/прогресс не показываются, пока не нажата.
5. Перейти на «Система» → «Снимки» (или где сейчас размещены снапшоты) → убедиться, что сохранение снапшота видит ровно те же отметки, что стоят на «Очистке» — сквозная проверка связки с `SystemTab.Snapshots.cs`, которую нельзя было сломать.

Если что-то из этого не совпадает с поведением до миграции — откатывать/чинить в этой же ветке до коммита финального шага.

- [ ] **Step 5: Финальный коммит (только если Step 1-4 все зелёные)**

```bash
git add -A
git status
git commit -m "test(debloater): пилот MVVM-миграции проверен вживую" --allow-empty
```

(`--allow-empty` — если Step 1-4 не потребовали правок сверх Task 1-3, это финальная контрольная точка без нового диффа.)

---

## После пилота

Не пушить, не мержить в `main`. Доложить пользователю результат (собрано/протестировано/что увидел живьём) и ждать решения — продолжать на следующую вкладку (по спеку — следующий кандидат по возрастанию сложности) или сначала пожить с этой на домашнем ПК.
