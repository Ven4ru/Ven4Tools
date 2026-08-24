# HistoryTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перевести вкладку «История» (`HistoryTab`) с code-behind на MVVM: вся логика (поиск, фильтры, сохранение флага, очистка, переустановка) переезжает в новый `HistoryViewModel`, `HistoryTab.xaml.cs` становится тонкой обёрткой, поведение не меняется (кроме одного согласованного отступления — см. Global Constraints).

**Architecture:** Тот же паттерн, что `CatalogTab`/`CatalogViewModel` и пилот `DebloaterTab`/`DebloaterViewModel` (2026-08-21): `UserControl` создаёт `ViewModel` в конструкторе, ставит его в `DataContext`, XAML биндится к свойствам/командам ViewModel. Публичный контракт `HistoryTab` (метод `RefreshAsync()`, который дёргает `MainWindow.NavigateToHistory`) сохраняется — становится однострочным форвардом на ViewModel.

**Tech Stack:** C# / .NET 8 / WPF, `Ven4Tools.ViewModels.RelayCommand` (уже есть в проекте), MSTest/FlaUI (`Ven4Tools.ClientUITests`) для UI-регресса, xUnit (`tests/Ven4Tools.Tests`) для юнит-тестов.

## Global Constraints

- Спек: `docs/superpowers/specs/2026-08-24-historytab-mvvm-design.md` — читать перед началом.
- Чистый рефакторинг, поведение 1:1, кроме одного согласованного отступления: `ReinstallCommand.CanExecute` блокирует ВСЕ кнопки «Переустановить» (не только нажатую), пока идёт хотя бы одна переустановка (флаг `IsReinstalling`) — было: блокировалась только нажатая кнопка.
- try/catch в `ReinstallAsync` (`OperationCanceledException`/`Exception` с прицельными сообщениями лога) переносится как есть, не заменяется на общий перехват `RelayCommand.FromAsync`.
- `MainWindow.xaml.cs` (`NavigateToHistory`) — **не трогать**, сигнатура `HistoryTab.RefreshAsync()` не меняется.
- Работа в локальной ветке `mvvm-full-migration` (уже активна, продолжение пилота). Коммитить локально после каждой задачи. **Не пушить** в `origin` без отдельного явного разрешения.
- `dotnet test` (юнит-тесты и UI-тесты) — только с явного разрешения пользователя каждый раз (см. память `feedback_no_tests_without_agreement`). `dotnet build` — можно свободно.
- Полный прогон `Ven4Tools.ClientUITests` — на машине **VenchWork** (`100.93.198.62`, логин `VenchWork`, репозиторий `C:\Users\VenchWork\Documents\GitHub\Ven4Tools`), не на домашнем ПК и не на ICL (полностью заменена VenchWork).
- Все тексты (комментарии, сообщения, тесты) — только на русском.

---

### Task 1: Создать `HistoryViewModel` + регрессионные юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/HistoryViewModel.cs`
- Create: `tests/Ven4Tools.Tests/HistoryViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Models.HistoryEntry` (существующий, поля `AppId`/`AppName`/`Source`/`Category`/`InstalledAt`/`Success`), `Ven4Tools.Services.InstallHistoryService.Instance` (существующий, `GetHistoryAsync()`/`ClearAsync()`/`Changed`), `Ven4Tools.Services.ProfileService.Current.SaveInstallHistory`/`Save()` (существующие), `Ven4Tools.Services.AppLogger.Write(string)` (существующий), `Ven4Tools.Services.CatalogLoaderService.State.Catalog` (существующий), `Ven4Tools.Services.InstallationService` (существующий: `InstallSemaphore` статическое поле, конструктор без параметров, `InstallAppAsync(AppInfo, string[], CancellationToken, IProgress<AppInstallProgress>, string, string?, Func<string,Task<bool>>)`), `Ven4Tools.Views.UiGuards.WarnIfInstallBusy()`/`ConfirmPackageManagerInstallAsync(string)` (существующие), `Ven4Tools.Models.AppInfo`/`AppCategory`/`AppInstallProgress` (существующие), `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync` (существующий).
- Produces: `Ven4Tools.ViewModels.HistoryViewModel` — публичные члены: `string SearchText` (get/set), `bool SuccessOnly` (get/set), `bool FailOnly` (get/set), `List<HistoryEntry> FilteredEntries` (get), `string HistoryCount` (get), `bool SaveHistory` (get/set), `bool IsReinstalling` (get), `RelayCommand ClearHistoryCommand`, `RelayCommand ReinstallCommand` (параметр команды — `HistoryEntry`), `Task RefreshAsync()`. Используется в Task 2.

- [ ] **Step 1: Написать `HistoryViewModel.cs`**

Полное содержимое `Ven4Tools/ViewModels/HistoryViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «История» — поиск, фильтры, флаг сохранения истории, очистка,
    /// переустановка. Перенесено из code-behind при MVVM-миграции клиента
    /// (2026-08-24, вторая вкладка после пилота DebloaterTab). Поведение не
    /// менялось, кроме <see cref="IsReinstalling"/> — см. спек
    /// docs/superpowers/specs/2026-08-24-historytab-mvvm-design.md.
    /// </summary>
    public sealed class HistoryViewModel : INotifyPropertyChanged
    {
        private List<HistoryEntry> _allEntries = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetField(ref _searchText, value)) ApplyFilter(); }
        }

        private bool _successOnly;
        public bool SuccessOnly
        {
            get => _successOnly;
            set { if (SetField(ref _successOnly, value)) ApplyFilter(); }
        }

        private bool _failOnly;
        public bool FailOnly
        {
            get => _failOnly;
            set { if (SetField(ref _failOnly, value)) ApplyFilter(); }
        }

        private List<HistoryEntry> _filteredEntries = new();
        public List<HistoryEntry> FilteredEntries
        {
            get => _filteredEntries;
            private set => SetField(ref _filteredEntries, value);
        }

        public string HistoryCount => FilteredEntries.Count.ToString();

        /// <summary>
        /// Флаг приватности: отключить запись истории можно было раньше только
        /// правкой profile.json руками. Управление вынесено туда, где история и
        /// показывается (не новая логика — перенос из HistoryTab.xaml.cs).
        /// </summary>
        public bool SaveHistory
        {
            get => ProfileService.Current.SaveInstallHistory;
            set
            {
                if (ProfileService.Current.SaveInstallHistory == value) return;
                ProfileService.Current.SaveInstallHistory = value;
                ProfileService.Save();
                AppLogger.Write(value
                    ? "[История] Запись истории установок включена"
                    : "[История] Запись истории установок отключена");
                OnPropertyChanged();
            }
        }

        // Согласованное отступление от 1:1 (см. спек): раньше блокировалась только
        // нажатая кнопка «Переустановить», теперь — все, пока идёт одна операция.
        // InstallSemaphore и так сериализует установки — это чуть строже старого
        // поведения, а не слабее.
        private bool _isReinstalling;
        public bool IsReinstalling
        {
            get => _isReinstalling;
            private set { if (SetField(ref _isReinstalling, value)) ReinstallCommand.RaiseCanExecuteChanged(); }
        }

        public RelayCommand ClearHistoryCommand { get; }
        public RelayCommand ReinstallCommand { get; }

        public HistoryViewModel()
        {
            ClearHistoryCommand = RelayCommand.FromAsync(async _ => await ClearHistoryAsync());
            ReinstallCommand = RelayCommand.FromAsync(
                async param => await ReinstallAsync(param as HistoryEntry),
                _ => !IsReinstalling);
        }

        public async Task RefreshAsync()
        {
            _allEntries = await InstallHistoryService.Instance.GetHistoryAsync();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = SearchText.Trim();
            var filtered = _allEntries.AsEnumerable();

            if (!string.IsNullOrEmpty(q))
                filtered = filtered.Where(e => e.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                            || e.Category.Contains(q, StringComparison.OrdinalIgnoreCase));

            if (SuccessOnly && !FailOnly) filtered = filtered.Where(e => e.Success);
            if (FailOnly && !SuccessOnly) filtered = filtered.Where(e => !e.Success);

            FilteredEntries = filtered.ToList();
            OnPropertyChanged(nameof(HistoryCount));
        }

        private async Task ClearHistoryAsync()
        {
            var r = MessageBox.Show("Очистить всю историю установок?",
                "Очистка", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            await InstallHistoryService.Instance.ClearAsync();
            await RefreshAsync();
        }

        private async Task ReinstallAsync(HistoryEntry? entry)
        {
            if (entry == null) return;

            // Проверяем занятость общего семафора установки ДО любых UI-мутаций —
            // семафор общий с каталогом и Windows Update.
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var catalog = CatalogLoaderService.State.Catalog;
            var catalogApp = catalog?.Apps.FirstOrDefault(a => a.Id == entry.AppId);
            if (catalogApp == null)
            {
                MessageBox.Show($"Приложение «{entry.AppName}» не найдено в текущем каталоге.",
                    "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var appInfo = new AppInfo
            {
                Id            = catalogApp.Id,
                DisplayName   = catalogApp.Name,
                Category      = AppCategory.Другое,
                AlternativeId = catalogApp.WingetId,
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                    ? new List<string> { catalogApp.DownloadUrl }
                    : new(),
                ChocoId = catalogApp.ChocoId,
                // SHA256 обязателен для установки по прямой ссылке при переустановке.
                Sha256 = catalogApp.Sha256
            };
            // Переопределение тихого флага (напр. AutoHotkey v2: "/silent" вместо "/S") —
            // без этого переустановка теряет override и падает на дефолтном "/S".
            if (!string.IsNullOrEmpty(catalogApp.SilentArgs))
                appInfo.SilentArgs = catalogApp.SilentArgs;

            AppLogger.Write($"🔄 Переустановка: {entry.AppName}...");

            var progress = new Progress<AppInstallProgress>(p =>
                AppLogger.Write($"  {p.Status}"));

            IsReinstalling = true;
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                using var installer = new InstallationService();
                using var cts = new CancellationTokenSource();
                var result = await installer.InstallAppAsync(
                    appInfo, new[] { "winget", "msstore" }, cts.Token, progress, "C:\\", null, Views.UiGuards.ConfirmPackageManagerInstallAsync);

                AppLogger.Write(result.Success
                    ? $"✅ {entry.AppName} переустановлен"
                    : $"❌ {entry.AppName}: {result.Message}");
            }
            catch (OperationCanceledException)
            {
                // InstallAppAsync гасит обычные ошибки и возвращает (false, сообщение),
                // но отмену пробрасывает наружу намеренно. Сюда же попадает таймаут
                // HttpClient при прямой загрузке (TaskCanceledException).
                AppLogger.Write($"⏹️ Переустановка {entry.AppName} прервана");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка переустановки {entry.AppName}: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsReinstalling = false;
            }
        }

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
Expected: 0 ошибок в новом файле (`HistoryTab.xaml.cs` ещё не переписан и продолжит компилироваться как раньше — новый `HistoryViewModel.cs` с ним никак не связан на этом шаге, ошибок быть не должно вообще).

- [ ] **Step 3: Написать регрессионные юнит-тесты**

Полное содержимое `tests/Ven4Tools.Tests/HistoryViewModelTests.cs`:

```csharp
using System.Linq;
using Ven4Tools.Models;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «История», перенесённая из code-behind в ViewModel
/// (2026-08-24). Реальная переустановка (InstallationService/CatalogLoaderService)
/// здесь не проверяется — только фильтр/поиск/счётчик, все чистые методы,
/// данные подставляются напрямую через RefreshAsync недоступен без сервиса,
/// поэтому фильтр проверяется через публичное состояние после конструктора
/// (пустой список) и через прямые сеттеры SearchText/SuccessOnly/FailOnly.
/// </summary>
public class HistoryViewModelTests
{
    [Fact]
    public void FilteredEntries_ПоУмолчанию_Пуст()
    {
        var vm = new HistoryViewModel();

        Assert.Empty(vm.FilteredEntries);
        Assert.Equal("0", vm.HistoryCount);
    }

    [Fact]
    public void SearchText_УстанавливаетсяИДоступноЧерезСвойство()
    {
        var vm = new HistoryViewModel();

        vm.SearchText = "firefox";

        Assert.Equal("firefox", vm.SearchText);
        // Список пуст (нет загруженной истории) — фильтр не должен падать на пустом наборе.
        Assert.Empty(vm.FilteredEntries);
    }

    [Fact]
    public void SuccessOnlyИFailOnly_ОбаВключеныОдновременно_НеПадают()
    {
        var vm = new HistoryViewModel();

        vm.SuccessOnly = true;
        vm.FailOnly = true;

        // Комбинация "оба включены" в исходной логике не фильтрует ни по одному
        // условию (эквивалент "показать всё") — регресс на пустом списке: просто
        // не должно быть исключения, список остаётся пустым.
        Assert.Empty(vm.FilteredEntries);
    }

    [Fact]
    public void SaveHistory_ЧтениеОтражаетProfileService()
    {
        var vm = new HistoryViewModel();
        bool original = Ven4Tools.Services.ProfileService.Current.SaveInstallHistory;

        try
        {
            vm.SaveHistory = !original;
            Assert.Equal(!original, Ven4Tools.Services.ProfileService.Current.SaveInstallHistory);
            Assert.Equal(!original, vm.SaveHistory);
        }
        finally
        {
            // Восстановить исходное значение — тест не должен менять состояние
            // profile.json на диске для остальных тестов сборки.
            vm.SaveHistory = original;
        }
    }

    [Fact]
    public void ReinstallCommand_НеПереустанавливает_БезЗапущеннойОперации()
    {
        var vm = new HistoryViewModel();

        Assert.False(vm.IsReinstalling);
        Assert.True(vm.ReinstallCommand.CanExecute(null),
            "Вне активной переустановки команда должна быть доступна.");
    }

    [Fact]
    public void ClearHistoryCommand_Существует_ИДоступнаПоУмолчанию()
    {
        var vm = new HistoryViewModel();

        Assert.True(vm.ClearHistoryCommand.CanExecute(null));
    }
}
```

- [ ] **Step 4: Запустить тесты (с разрешения пользователя)**

Спросить пользователя явно: «Можно запустить `dotnet test tests/Ven4Tools.Tests --filter HistoryViewModelTests`?» Только после «да»:

Run: `dotnet test tests/Ven4Tools.Tests --filter FullyQualifiedName~HistoryViewModelTests`
Expected: все 6 тестов из Step 3 зелёные.

⚠️ `SaveHistory_ЧтениеОтражаетProfileService` временно меняет `ProfileService.Current.SaveInstallHistory` в реальном `profile.json` на машине, где идёт тест, но восстанавливает исходное значение в `finally` — если тест упадёт ДО восстановления, проверить `profile.json` вручную и вернуть `SaveInstallHistory` в исходное состояние.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/ViewModels/HistoryViewModel.cs tests/Ven4Tools.Tests/HistoryViewModelTests.cs
git commit -m "feat(history): HistoryViewModel + регрессионные юнит-тесты"
```

---

### Task 2: Переписать `HistoryTab.xaml`/`HistoryTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/HistoryTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/HistoryTab.xaml.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.HistoryViewModel` (Task 1) — все публичные члены, перечисленные в Task 1.
- Produces: `HistoryTab` с публичным контрактом, идентичным исходному: `Task RefreshAsync()` — используется `MainWindow.NavigateToHistory` (не меняется).

- [ ] **Step 1: Переписать `HistoryTab.xaml`**

Полное содержимое `Ven4Tools/Views/Tabs/HistoryTab.xaml` (изменения — только `IsChecked`/`ItemsSource`/`Text`/`Command`/`CommandParameter` у уже существующих элементов, разметка/стили не трогаются):

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.HistoryTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Margin="0,0,0,16">
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                <TextBlock Text="📋 История установок" FontSize="22" FontWeight="Bold"
                           Foreground="{DynamicResource TextPrimary}"/>
                <Border Background="{DynamicResource AccentColor}" CornerRadius="10"
                        Padding="8,2" Margin="12,4,0,4" VerticalAlignment="Center">
                    <TextBlock x:Name="txtHistoryCount" Text="{Binding HistoryCount}"
                               FontSize="12" FontWeight="Bold" Foreground="White"/>
                </Border>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <CheckBox x:Name="chkSaveHistory" Content="Сохранять историю"
                          VerticalAlignment="Center" Margin="0,0,14,0"
                          Foreground="{DynamicResource TextPrimary}"
                          ToolTip="Когда снято — новые установки не записываются в историю. Уже сохранённые записи остаются, удалить их можно кнопкой «Очистить»."
                          IsChecked="{Binding SaveHistory, Mode=TwoWay}"/>
                <Button x:Name="btnClearHistory" Content="🗑️ Очистить"
                        Style="{StaticResource DangerButtonStyle}"
                        ToolTip="После подтверждения удалит сохранённую историю установок."
                        Height="32" Padding="12,0" Margin="0,0,0,0"
                        Command="{Binding ClearHistoryCommand}"/>
            </StackPanel>
        </Grid>

        <!-- Filters -->
        <Grid Grid.Row="1" Margin="0,0,0,12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBox x:Name="txtHistorySearch" Height="32" Margin="0,0,8,0"
                     Background="{DynamicResource CardBackground}"
                     Foreground="{DynamicResource TextPrimary}"
                     BorderBrush="{DynamicResource BorderBrush}"
                     VerticalContentAlignment="Center" Padding="8,0"
                     Tag="🔍 Поиск в истории..."
                     TextChanged="TxtHistorySearch_TextChanged"/>
            <ToggleButton x:Name="togSuccessOnly" Grid.Column="1" Content="✅ Успешные"
                          Height="32" Padding="10,0" Margin="0,0,6,0"
                          IsChecked="{Binding SuccessOnly, Mode=TwoWay}"/>
            <ToggleButton x:Name="togFailOnly" Grid.Column="2" Content="❌ Неудачные"
                          Height="32" Padding="10,0"
                          IsChecked="{Binding FailOnly, Mode=TwoWay}"/>
        </Grid>

        <!-- History list -->
        <ItemsControl x:Name="lstHistory" Grid.Row="2" ItemsSource="{Binding FilteredEntries}">
            <ItemsControl.Template>
                <ControlTemplate>
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <ItemsPresenter/>
                    </ScrollViewer>
                </ControlTemplate>
            </ItemsControl.Template>
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Border Background="{DynamicResource CardBackground}"
                            CornerRadius="8" Padding="12,8" Margin="0,0,0,4">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>

                            <!-- Status icon -->
                            <TextBlock Grid.Column="0" Text="{Binding StatusIcon}"
                                       FontSize="16" VerticalAlignment="Center"
                                       Margin="0,0,10,0"/>

                            <!-- install App [date] -->
                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <!-- Main action line: "install Firefox  [28.05.2026 23:45]" -->
                                <TextBlock FontFamily="Consolas" FontSize="13">
                                    <Run Text="{Binding ActionVerb, Mode=OneWay}"
                                         Foreground="{DynamicResource TextSecondary}"/>
                                    <Run Text="{Binding AppName, Mode=OneWay}"
                                         Foreground="{DynamicResource TextPrimary}"
                                         FontWeight="SemiBold"/>
                                </TextBlock>
                                <TextBlock FontSize="11"
                                           Foreground="{DynamicResource TextSecondary}"
                                           Margin="0,2,0,0">
                                    <Run Text="{Binding SourceLabel, Mode=OneWay}"/>
                                    <Run Text=" · "/>
                                    <Run Text="{Binding Category, Mode=OneWay}"/>
                                    <Run Text=" · "/>
                                    <Run Text="{Binding DateLabel, Mode=OneWay}"/>
                                </TextBlock>
                            </StackPanel>

                            <!-- spacer -->

                            <!-- Reinstall -->
                            <Button Grid.Column="3" Content="🔄"
                                    Height="28" Width="36" FontSize="14"
                                    VerticalAlignment="Center"
                                    ToolTip="Повторно установит это приложение из доступного источника."
                                    Command="{Binding DataContext.ReinstallCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                    CommandParameter="{Binding}"/>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Переписать `HistoryTab.xaml.cs`**

Полное содержимое `Ven4Tools/Views/Tabs/HistoryTab.xaml.cs`:

```csharp
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «История» — тонкая обёртка над <see cref="HistoryViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-24, вторая
    /// вкладка после пилота DebloaterTab). Публичный контракт (RefreshAsync)
    /// сохранён без изменений — <c>MainWindow.NavigateToHistory</c> обращается
    /// к нему напрямую.
    /// </summary>
    public partial class HistoryTab : UserControl
    {
        private readonly HistoryViewModel _viewModel = new();
        private bool _historySubscribed;

        public HistoryTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            // Подписка на Changed — в Loaded с флагом, отписка в Unloaded: вкладка
            // кэшируется в MainWindow и переиспользуется, поэтому после Unloaded нужно
            // подписываться заново при каждом показе (иначе живое обновление истории
            // пропадало после первого ухода с вкладки). Отписка снимает утечку.
            Loaded += HistoryTab_Loaded;
            Unloaded += HistoryTab_Unloaded;

            txtHistorySearch.GotFocus  += (_, _) => { if (txtHistorySearch.Text == (string)txtHistorySearch.Tag) txtHistorySearch.Text = ""; };
            txtHistorySearch.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(txtHistorySearch.Text)) txtHistorySearch.Text = (string)txtHistorySearch.Tag; };
            txtHistorySearch.Text = (string)txtHistorySearch.Tag;
        }

        private async void HistoryTab_Loaded(object sender, RoutedEventArgs e)
        {
            // Переподписка при каждом показе (после Unloaded подписка снимается).
            // Флаг защищает от повторной подписки, если Loaded сработает дважды подряд.
            if (!_historySubscribed)
            {
                InstallHistoryService.Instance.Changed += OnHistoryChanged;
                _historySubscribed = true;
            }
            await RefreshAsync();
        }

        private void HistoryTab_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_historySubscribed)
            {
                InstallHistoryService.Instance.Changed -= OnHistoryChanged;
                _historySubscribed = false;
            }
        }

        private void OnHistoryChanged() =>
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { AppLogger.Write(ex.Message); }
            });

        // Плейсхолдер в txtHistorySearch — не настоящий поиск, форвардим в
        // ViewModel только реальный запрос пользователя.
        private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = txtHistorySearch.Text;
            _viewModel.SearchText = text == (string)txtHistorySearch.Tag ? "" : text;
        }

        public Task RefreshAsync() => _viewModel.RefreshAsync();
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/Tabs/HistoryTab.xaml Ven4Tools/Views/Tabs/HistoryTab.xaml.cs
git commit -m "refactor(history): HistoryTab — тонкая обёртка над HistoryViewModel"
```

---

### Task 3: Новый UI-регресс-тест `HistoryTab_ПоискФильтрОчистка`

**Files:**
- Modify: `Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs`

**Interfaces:**
- Consumes: `AppSession` (существующий, тот же helper, что используют остальные тесты в файле), `Retry.WhileFalse` (FlaUI.Core.Tools, уже используется в `ClickAndWaitReEnabled`), `AutomationId` элементов `HistoryTab.xaml`: `btnHistoryTab` (навигация, `MainWindow.xaml`), `txtHistorySearch`, `togSuccessOnly`, `togFailOnly`, `txtHistoryCount`, `btnClearHistory`.
- Produces: тестовый метод `DebloaterTab_ВыбратьВсеИСброс`-уровня (тот же класс) — используется Task 4 (запуск).

- [ ] **Step 1: Добавить бэкап/восстановление `install_history.json` в `ClassInitialize`/`ClassCleanup`**

`Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs` — новый тест реально жмёт «Очистить» (безопасно: трогает только собственный JSON-файл приложения, не систему), поэтому добавляем backup/restore рядом с уже существующим backup `profile.json`. Заменить блок `ClassInitialize`/`ClassCleanup` (строки 20-52 в текущем файле) на:

```csharp
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");
        private static readonly string HistoryPath = Path.Combine(SettingsDir, "install_history.json");

        private static string? _profileBackup; private static bool _profileExisted;
        private static string? _historyBackup; private static bool _historyExisted;
        private static AppSession? _session;
        private static string? _launchError;
        private static readonly TimeSpan T = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            Directory.CreateDirectory(SettingsDir);
            _profileExisted = File.Exists(ProfilePath);
            if (_profileExisted) _profileBackup = File.ReadAllText(ProfilePath);
            File.WriteAllText(ProfilePath, "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true}");

            _historyExisted = File.Exists(HistoryPath);
            if (_historyExisted) _historyBackup = File.ReadAllText(HistoryPath);

            try { _session = AppSession.Launch(); }
            catch (Exception ex) { _launchError = ex.Message; _session = null; }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;
            try
            {
                if (_profileExisted) File.WriteAllText(ProfilePath, _profileBackup!);
                else if (File.Exists(ProfilePath)) File.Delete(ProfilePath);

                if (_historyExisted) File.WriteAllText(HistoryPath, _historyBackup!);
                else if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
            }
            catch { }
        }
```

(Единственное изменение по сути — добавлены `HistoryPath`/`_historyBackup`/`_historyExisted` и их сохранение/восстановление, копия уже существующего паттерна для `ProfilePath`.)

- [ ] **Step 2: Написать тест**

Добавить в конец класса `Phase3RemainingTabsTests` (после `DebloaterTab_ВыбратьВсеИСброс`, перед закрывающей `}` класса):

```csharp
        [TestMethod]
        public void HistoryTab_ПоискФильтрОчистка()
        {
            var s = Require();
            var historyBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnHistoryTab"));
            Assert.IsNotNull(historyBtn, "Не найдена кнопка вкладки «История».");
            historyBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            var search = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtHistorySearch"));
            Assert.IsNotNull(search, "Не найдено поле поиска (История).");
            var count = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtHistoryCount"));
            Assert.IsNotNull(count, "Не найден счётчик записей (История).");

            // Поиск заведомо несуществующего текста должен обнулить список —
            // единственное утверждение, не зависящее от того, есть ли реальная
            // история на тестовой машине.
            search!.AsTextBox().Text = "несуществующее-приложение-zzz-12345";
            Thread.Sleep(400);
            Assert.AreEqual("0", count!.AsLabel().Text, "Поиск по несуществующему тексту должен обнулить счётчик.");

            search.AsTextBox().Text = "";
            Thread.Sleep(300);

            var successOnly = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("togSuccessOnly"));
            Assert.IsNotNull(successOnly, "Не найден переключатель «Успешные» (История).");
            successOnly!.AsToggleButton().Toggle();
            Thread.Sleep(300);

            var failOnly = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("togFailOnly"));
            Assert.IsNotNull(failOnly, "Не найден переключатель «Неудачные» (История).");
            failOnly!.AsToggleButton().Toggle();
            Thread.Sleep(300);

            // Вернуть оба переключателя в исходное состояние перед очисткой —
            // иначе следующая проверка счётчика после очистки видит "0" из-за
            // фильтра, а не из-за реальной очистки.
            successOnly.AsToggleButton().Toggle();
            failOnly.AsToggleButton().Toggle();
            Thread.Sleep(300);

            var clearBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnClearHistory"));
            Assert.IsNotNull(clearBtn, "Не найдена кнопка «Очистить» (История).");
            clearBtn!.AsButton().Invoke();

            // Диалог подтверждения — модальный MessageBox, не элемент MainWindow,
            // ищем его на рабочем столе по заголовку тем же приёмом, что
            // AppSession.WaitForMainWindow ищет главное окно.
            var confirmWindow = Retry.WhileNull(
                () => s.Automation.GetDesktop()
                    .FindFirstChild(cf => cf.ByControlType(ControlType.Window).And(cf.ByName("Очистка")))
                    ?.AsWindow(),
                timeout: TimeSpan.FromSeconds(10),
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;
            Assert.IsNotNull(confirmWindow, "Не найден диалог подтверждения очистки истории.");
            var yesBtn = confirmWindow!.FindFirstDescendant(cf => cf.ByName("Да"));
            Assert.IsNotNull(yesBtn, "Не найдена кнопка «Да» в диалоге подтверждения.");
            yesBtn!.AsButton().Invoke();
            Thread.Sleep(500);

            Assert.AreEqual("0", count.AsLabel().Text, "После подтверждённой очистки счётчик истории должен быть 0.");

            // btnReinstall (🔄 на строках) НЕ кликаем — реально ставит приложение
            // через сеть, это риск-код-ревью, не безопасная кнопка. После очистки
            // выше список и так пуст — строк с этой кнопкой не осталось.
        }
```

- [ ] **Step 3: Добавить недостающий `using`**

Текущие `using` в `Phase3RemainingTabsTests.cs`: `System`, `System.IO`, `System.Threading`, `FlaUI.Core.AutomationElements`, `FlaUI.Core.Tools`, `Microsoft.VisualStudio.TestTools.UnitTesting`. Для `ControlType.Window` в Step 2 нужен `FlaUI.Core.Definitions` — добавить строку `using FlaUI.Core.Definitions;` после `using FlaUI.Core.AutomationElements;`.

- [ ] **Step 4: Проверить сборку тестового проекта**

Run: `dotnet build Ven4Tools.ClientUITests -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs
git commit -m "test(ui): регресс-тест HistoryTab — поиск, фильтры, очистка"
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
Expected: было 378/378 после пилота (см. память `project_ven4tools_mvvm_migration_pilot_2026_08_21`) + 6 новых из `HistoryViewModelTests` = 384/384. Если число другое — разбираться, не игнорировать расхождение.

- [ ] **Step 3: Спросить разрешение и прогнать новый UI-тест изолированно на VenchWork**

Спросить: «Можно прогнать `HistoryTab_ПоискФильтрОчистка` живым запуском клиента на VenchWork (`100.93.198.62`)?» После «да», по рецепту `schtasks /it /rl HIGHEST` (см. память `reference_ui_tests_known_issues_20260724` и карточку VenchWork в `reference_device_topology`) — перенос ветки на VenchWork через `git bundle` (та же процедура, что для пилота на ICL, см. память `project_ven4tools_mvvm_migration_pilot_2026_08_21`, раздел «Грабли для памяти», т.к. `mvvm-full-migration` не запушена в origin):

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests --filter FullyQualifiedName~HistoryTab_ПоискФильтрОчистка`
Expected: 1/1 пройден.

- [ ] **Step 4: Спросить разрешение и прогнать полный `Ven4Tools.ClientUITests` на VenchWork**

Спросить: «Можно прогнать весь `Ven4Tools.ClientUITests` на VenchWork?» После «да»:

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests`
Expected: не хуже базового результата до этой задачи (61/0/3 на момент round 40, см. память `audit_2026_08_20_round40` — сверить актуальный baseline, если он изменился после пилота). Новых падений сверх известных флейков (`reference_ui_tests_known_issues_20260724`) быть не должно.

- [ ] **Step 5: Живой ручной клик (обязателен, не пропускать)**

Запустить клиент на домашнем ПК (`dotnet run --project Ven4Tools` либо уже собранный `_release`), открыть вкладку «История»:

1. Ввести в поиск текст, который точно ничего не найдёт — список пустеет, счётчик показывает 0.
2. Очистить поиск — список возвращается.
3. Включить «✅ Успешные» — остаются только успешные записи (если в истории есть и успешные, и неудачные).
4. Включить дополнительно «❌ Неудачные» (оба нажаты) — список возвращается к полному (текущее поведение: комбинация "оба" не фильтрует).
5. Выключить оба фильтра.
6. Нажать «🔄» у одной реальной записи истории (не пустой список, желательно записей — 2+) — приложение либо переустанавливается, либо лог показывает «не найдено в каталоге» / ошибку — в любом случае без падения клиента (`crash_last.json` не появляется — проверить отдельно, см. урок пилота: зелёный прогон не гарантирует отсутствие тихого сбоя). Пока идёт переустановка, попробовать кликнуть «🔄» у ДРУГОЙ записи — кнопка должна быть недоступна (`IsReinstalling`, согласованное отступление от 1:1).
7. Нажать «🗑️ Очистить», подтвердить — список пустеет, счётчик 0.
8. Снять галку «Сохранять историю», установить любое приложение из каталога — новая запись НЕ должна появиться в истории. Включить галку обратно.

Если что-то из этого не совпадает с ожидаемым — чинить в этой же ветке до финального коммита.

- [ ] **Step 6: Финальный коммит (только если Step 1-5 все зелёные)**

```bash
git add -A
git status
git commit -m "test(history): MVVM-миграция HistoryTab проверена вживую" --allow-empty
```

---

## После задачи

Не пушить, не мержить в `main`. Доложить пользователю результат (собрано/протестировано/что увидел живьём) и ждать решения — продолжать на следующую вкладку или сначала пожить с этой на домашнем ПК.
