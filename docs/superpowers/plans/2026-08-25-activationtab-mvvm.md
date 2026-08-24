# ActivationTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перевести вкладку «Активация» (`ActivationTab`) с code-behind на MVVM: чекбокс согласия, статус лицензий Windows/Office, три команды переезжают в новый `ActivationViewModel`, `ActivationTab.xaml.cs` становится тонкой обёрткой, поведение не меняется.

**Architecture:** Тот же паттерн, что `DebloaterViewModel`/`HistoryViewModel`/`AboutViewModel`: `UserControl` создаёт `ViewModel` в конструкторе, ставит в `DataContext`, XAML биндится к свойствам/командам.

**Tech Stack:** C# / .NET 8 / WPF, `Ven4Tools.ViewModels.RelayCommand`, `System.Management` (WMI), xUnit.

## Global Constraints

- Спек: `docs/superpowers/specs/2026-08-25-activationtab-mvvm-design.md` — читать перед началом.
- Чистый рефакторинг, поведение 1:1, кроме одной механической адаптации: `this.Dispatcher.Invoke` → `System.Windows.Application.Current.Dispatcher.Invoke` (ViewModel не `DependencyObject`).
- `MainWindow.xaml.cs` — не трогать.
- Ветка `mvvm-activationtab` (уже создана и активна). Коммитить локально после каждой задачи. Пуш — только после полной верификации (см. Task 3), без дополнительного вопроса пользователю (автономная ночная сессия).
- `dotnet test`/`ClientUITests` — только на VenchWork (общее разрешение уже дано в этой сессии), НЕ локально.
- Все тексты — только на русском, никаких упоминаний Claude/AI. Каждый коммит проверять на `Claude-Session:`-трейлер перед финальным пушем (см. память `feedback_no_claude_attribution` — два прошлых инцидента).
- Существующий UI-тест `ActivationTab_ПроверитьСтатус` (`Ven4Tools.ClientUITests/Phase3RemainingTabsTests.cs`) должен остаться зелёным — новый тест не создаётся.

---

### Task 1: Создать `ActivationViewModel` + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/ActivationViewModel.cs`
- Create: `tests/Ven4Tools.Tests/ActivationViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Services.AppLogger.Write(string)`, `Ven4Tools.Services.TrustedExecutablePaths.CScriptExe`, `Ven4Tools.Views.MasGuideWindow(string product)` (существующий конструктор, принимает "Windows"/"Office"), `Ven4Tools.ViewModels.RelayCommand`/`RelayCommand.FromAsync`, `System.Management.ManagementObjectSearcher`.
- Produces: `Ven4Tools.ViewModels.ActivationViewModel` — публичные члены: `Func<Window?>? OwnerWindowProvider`, `bool ConsentGiven` (get/set), `string WindowsStatusText` (get), `Brush WindowsStatusBrush` (get), `string OfficeStatusText` (get), `Brush OfficeStatusBrush` (get), `bool IsCheckingStatus` (get), `RelayCommand ActivateWindowsCommand`, `RelayCommand ActivateOfficeCommand`, `RelayCommand CheckStatusCommand`, `Task CheckActivationStatusAsync()`. Используется в Task 2.

- [ ] **Step 1: Написать `ActivationViewModel.cs`**

Полное содержимое `Ven4Tools/ViewModels/ActivationViewModel.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Services;
using Ven4Tools.Views;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «Активация» — согласие, статус лицензий Windows/Office, ссылки на
    /// сторонний инструмент активации. Перенесено из code-behind при MVVM-миграции
    /// (2026-08-25, четвёртая вкладка после DebloaterTab/HistoryTab/AboutTab),
    /// поведение не менялось — кроме способа попасть в UI-поток: у ViewModel нет
    /// собственного Dispatcher, используется Application.Current.Dispatcher.
    /// </summary>
    public sealed class ActivationViewModel : INotifyPropertyChanged
    {
        private static readonly TimeSpan OfficeCheckTimeout = TimeSpan.FromSeconds(30);

        public Func<Window?>? OwnerWindowProvider { get; set; }

        private bool _consentGiven;
        public bool ConsentGiven
        {
            get => _consentGiven;
            set => SetField(ref _consentGiven, value);
        }

        private string _windowsStatusText = "Проверка...";
        public string WindowsStatusText { get => _windowsStatusText; private set => SetField(ref _windowsStatusText, value); }

        private Brush _windowsStatusBrush = (Brush)new BrushConverter().ConvertFromString("#FFFFFFFF")!;
        public Brush WindowsStatusBrush { get => _windowsStatusBrush; private set => SetField(ref _windowsStatusBrush, value); }

        private string _officeStatusText = "Проверка...";
        public string OfficeStatusText { get => _officeStatusText; private set => SetField(ref _officeStatusText, value); }

        private Brush _officeStatusBrush = (Brush)new BrushConverter().ConvertFromString("#FFFFFFFF")!;
        public Brush OfficeStatusBrush { get => _officeStatusBrush; private set => SetField(ref _officeStatusBrush, value); }

        private bool _isCheckingStatus;
        public bool IsCheckingStatus
        {
            get => _isCheckingStatus;
            private set { if (SetField(ref _isCheckingStatus, value)) CheckStatusCommand.RaiseCanExecuteChanged(); }
        }

        public RelayCommand ActivateWindowsCommand { get; }
        public RelayCommand ActivateOfficeCommand { get; }
        public RelayCommand CheckStatusCommand { get; }

        public ActivationViewModel()
        {
            ActivateWindowsCommand = new RelayCommand(_ => ActivateWindows());
            ActivateOfficeCommand = new RelayCommand(_ => ActivateOffice());
            CheckStatusCommand = RelayCommand.FromAsync(async _ => await RunCheckStatusAsync(), _ => !IsCheckingStatus);
        }

        // Открывает сайт и окно-помощник для активации Windows
        private void ActivateWindows()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://massgrave.dev") { UseShellExecute = true });
                AppLogger.Write("🌐 Открыт сайт для управления лицензией Windows");
                var guide = new MasGuideWindow("Windows") { Owner = OwnerWindowProvider?.Invoke() };
                guide.Show();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        // Открывает сайт и окно-помощник для активации Office
        private void ActivateOffice()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://massgrave.dev") { UseShellExecute = true });
                AppLogger.Write("🌐 Открыт сайт для управления лицензией Office");
                var guide = new MasGuideWindow("Office") { Owner = OwnerWindowProvider?.Invoke() };
                guide.Show();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
        }

        private async Task RunCheckStatusAsync()
        {
            IsCheckingStatus = true;
            try
            {
                await CheckActivationStatusAsync();
                AppLogger.Write("🔄 Статус активации обновлён");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                IsCheckingStatus = false;
            }
        }

        public async Task CheckActivationStatusAsync()
        {
            try
            {
                WindowsStatusText = "Проверка...";
                OfficeStatusText = "Проверка...";

                await Task.Run(() =>
                {
                    try
                    {
                        using (var searcher = CreateLicensingSearcher())
                        using (var results = searcher.Get())
                        {
                            foreach (ManagementBaseObject obj in results)
                            using (obj)
                            {
                                int status = Convert.ToInt32(obj["LicenseStatus"]);
                                string name = obj["Name"]?.ToString() ?? "";

                                if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        WindowsStatusText = status switch
                                        {
                                            1 => "✅ Активирована",
                                            0 => "❌ Не активирована",
                                            _ => "⚠️ Неизвестно"
                                        };
                                        WindowsStatusBrush = status == 1 ?
                                            new SolidColorBrush(Colors.LightGreen) :
                                            new SolidColorBrush(Colors.LightCoral);
                                    });
                                    return;
                                }
                            }
                        }
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            WindowsStatusText = "⚠️ Не обнаружена";
                            WindowsStatusBrush = new SolidColorBrush(Colors.Orange);
                        });
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            WindowsStatusText = "⚠️ Ошибка";
                            WindowsStatusBrush = new SolidColorBrush(Colors.Orange);
                            AppLogger.Write($"❌ Ошибка проверки Windows: {ex.Message}");
                        });
                    }
                });

                await Task.Run(() => CheckOfficeActivationAsync());
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка проверки статуса: {ex.Message}");
            }
        }

        private async Task CheckOfficeActivationAsync()
        {
            try
            {
                // OSPP.VBS — официальный инструмент проверки лицензии Office (2010–2024, 365)
                string[] osppPaths =
                {
                    @"C:\Program Files\Microsoft Office\Office16\OSPP.VBS",
                    @"C:\Program Files (x86)\Microsoft Office\Office16\OSPP.VBS",
                    @"C:\Program Files\Microsoft Office\Office15\OSPP.VBS",
                    @"C:\Program Files (x86)\Microsoft Office\Office15\OSPP.VBS",
                    @"C:\Program Files\Microsoft Office\Office14\OSPP.VBS",
                    @"C:\Program Files (x86)\Microsoft Office\Office14\OSPP.VBS",
                };

                string? osppPath = null;
                foreach (var p in osppPaths)
                    if (File.Exists(p)) { osppPath = p; break; }

                if (osppPath != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = TrustedExecutablePaths.CScriptExe,
                        Arguments = $"//NoLogo \"{osppPath}\" /dstatus",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    // Таймаут: раньше WaitForExit() был без ограничения — зависший OSPP.VBS
                    // (повреждённая установка Office/недоступный KMS-хост) держал вкладку
                    // в «Проверка...» бесконечно, кнопка «Проверить статус» не разблокировалась.
                    string output;
                    using var timeoutCts = new CancellationTokenSource(OfficeCheckTimeout);
                    using (var proc = Process.Start(psi)!)
                    {
                        using var reg = timeoutCts.Token.Register(() =>
                            { try { proc.Kill(entireProcessTree: true); } catch { } });
                        try
                        {
                            output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                            await proc.WaitForExitAsync(timeoutCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            SetOfficeStatusOnUI("⚠️ Проверка не завершилась", null);
                            return;
                        }
                    }

                    bool hasProducts = output.Contains("SKU ID") || output.Contains("LICENSE NAME");
                    if (!hasProducts)
                    {
                        SetOfficeStatusOnUI("❓ Office не обнаружен", null);
                        return;
                    }

                    if (output.Contains("---LICENSED---"))
                        SetOfficeStatusOnUI("✅ Активирован", true);
                    else if (output.Contains("---UNLICENSED---") || output.Contains("NON_GENUINE"))
                        SetOfficeStatusOnUI("❌ Не активирован", false);
                    else if (output.Contains("OOB_GRACE") || output.Contains("NOTIFICATION"))
                        SetOfficeStatusOnUI("⚠️ Пробный период", null);
                    else
                        SetOfficeStatusOnUI("⚠️ Статус неопределён", null);
                    return;
                }

                // Запасной вариант: WMI SoftwareLicensingProduct
                using var searcher = CreateLicensingSearcher();
                using var results = searcher.Get();
                foreach (ManagementBaseObject obj in results)
                using (obj)
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (name.Contains("Office", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase))
                    {
                        int status = Convert.ToInt32(obj["LicenseStatus"]);
                        SetOfficeStatusOnUI(status == 1 ? "✅ Активирован" : "❌ Не активирован", status == 1);
                        return;
                    }
                }

                // Финальный фоллбэк: просто проверяем установлен ли Office
                string[] regPaths =
                {
                    @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration",
                    @"SOFTWARE\Microsoft\Office\16.0\Common\Licensing",
                    @"SOFTWARE\Microsoft\Office\15.0\Common\Licensing",
                };
                bool installed = false;
                foreach (var regPath in regPaths)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                    if (key != null) { installed = true; break; }
                }

                SetOfficeStatusOnUI(installed ? "⚠️ Статус неизвестен" : "❓ Office не обнаружен", null);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OfficeStatusText = "⚠️ Ошибка";
                    OfficeStatusBrush = new SolidColorBrush(Colors.Orange);
                    AppLogger.Write($"❌ Ошибка проверки Office: {ex.Message}");
                });
            }
        }

        private void SetOfficeStatusOnUI(string text, bool? isActivated)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                OfficeStatusText = text;
                OfficeStatusBrush = isActivated switch
                {
                    true  => new SolidColorBrush(Colors.LightGreen),
                    false => new SolidColorBrush(Colors.LightCoral),
                    null  => new SolidColorBrush(Colors.Orange)
                };
            });
        }

        // Единый WMI-запрос лицензий (Windows и Office) — используется при проверке
        // статуса активации и в запасном варианте для Office.
        internal static ManagementObjectSearcher CreateLicensingSearcher() =>
            new("SELECT LicenseStatus, Name FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL");

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

Note on `CreateLicensingSearcher` visibility: changed from `private static` to `internal static` (same reasoning as `HistoryViewModel.FormatLastLines`/`AboutViewModel.BuildEntries` in prior tabs — gives the test project a seam without changing runtime behavior; `InternalsVisibleTo("Ven4Tools.Tests")` already exists in `Ven4Tools/Properties/AssemblyInfo.cs`, verify this if the build fails).

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок в новом файле (`ActivationTab.xaml.cs` пока не переписан, ошибок там быть не должно — новый файл ни на что старое не влияет).

- [ ] **Step 3: Написать юнит-тесты**

Полное содержимое `tests/Ven4Tools.Tests/ActivationViewModelTests.cs`:

```csharp
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «Активация», перенесённая из code-behind в ViewModel
/// (2026-08-25). Реальные WMI-запросы/Process.Start (CheckActivationStatusAsync,
/// ActivateWindows/OfficeCommand) здесь не проверяются — только конструирование,
/// биндинг-состояние и построение WMI-запроса как строки.
/// </summary>
public class ActivationViewModelTests
{
    [Fact]
    public void ConsentGiven_ПоУмолчанию_False()
    {
        var vm = new ActivationViewModel();

        Assert.False(vm.ConsentGiven);
    }

    [Fact]
    public void ConsentGiven_МожноУстановитьВTrue()
    {
        var vm = new ActivationViewModel();

        vm.ConsentGiven = true;

        Assert.True(vm.ConsentGiven);
    }

    [Fact]
    public void ConsentGiven_ПоднимаетPropertyChanged()
    {
        var vm = new ActivationViewModel();
        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.ConsentGiven = true;

        Assert.Contains(nameof(vm.ConsentGiven), raised);
    }

    [Fact]
    public void WindowsStatusText_ИOfficeStatusText_ПоУмолчанию_Проверка()
    {
        var vm = new ActivationViewModel();

        Assert.Equal("Проверка...", vm.WindowsStatusText);
        Assert.Equal("Проверка...", vm.OfficeStatusText);
    }

    [Fact]
    public void IsCheckingStatus_ПоУмолчанию_False_КомандаДоступна()
    {
        var vm = new ActivationViewModel();

        Assert.False(vm.IsCheckingStatus);
        Assert.True(vm.CheckStatusCommand.CanExecute(null));
    }

    [Fact]
    public void CreateLicensingSearcher_СтроитЗапросПоSoftwareLicensingProduct()
    {
        var searcher = ActivationViewModel.CreateLicensingSearcher();

        Assert.Contains("SoftwareLicensingProduct", searcher.Query.QueryString);
        Assert.Contains("LicenseStatus", searcher.Query.QueryString);
        Assert.Contains("PartialProductKey IS NOT NULL", searcher.Query.QueryString);
    }

    [Fact]
    public void ActivateWindowsCommand_И_ActivateOfficeCommand_ДоступныПоУмолчанию()
    {
        var vm = new ActivationViewModel();

        Assert.True(vm.ActivateWindowsCommand.CanExecute(null));
        Assert.True(vm.ActivateOfficeCommand.CanExecute(null));
    }
}
```

- [ ] **Step 4: Запустить тесты на VenchWork (разрешение уже дано в этой сессии)**

Run (на VenchWork, через уже отработанный в этой сессии рецепт переноса ветки git bundle + `dotnet test`): `dotnet test tests/Ven4Tools.Tests --filter FullyQualifiedName~ActivationViewModelTests`
Expected: все 7 тестов зелёные.

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/ViewModels/ActivationViewModel.cs tests/Ven4Tools.Tests/ActivationViewModelTests.cs
git commit -m "feat(activation): ActivationViewModel + юнит-тесты"
```

---

### Task 2: Переписать `ActivationTab.xaml`/`ActivationTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/ActivationTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/ActivationTab.xaml.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.ActivationViewModel` (Task 1) — все публичные члены.
- Produces: `ActivationTab` без публичного контракта сверх конструктора.

- [ ] **Step 1: Переписать `ActivationTab.xaml`**

Полное содержимое `Ven4Tools/Views/Tabs/ActivationTab.xaml` (меняются: `IsChecked`/`IsEnabled`/`Command` у чекбокса и трёх кнопок, `Text`/`Foreground` у двух статусных `TextBlock`; остальная разметка не трогается):

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.ActivationTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="20">
            <TextBlock Text="Управление лицензиями" FontSize="24" FontWeight="Bold"
                       Foreground="{DynamicResource TextPrimary}" Margin="0,0,0,10"/>

            <TextBlock Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,0,0,20">
                <Run Text="Ven4Tools открывает официальный сайт стороннего инструмента с открытым кодом. "/>
                <Run Text="Вся дальнейшая работа выполняется пользователем самостоятельно."/>
            </TextBlock>

            <!-- Чекбокс согласия -->
            <Border Background="#1A1E2A" CornerRadius="8" Padding="14,10" Margin="0,0,0,20"
                    BorderBrush="#FF6B35" BorderThickness="1">
                <CheckBox x:Name="chkActivationConsent"
                          Foreground="{DynamicResource TextPrimary}"
                          FontSize="12"
                          IsChecked="{Binding ConsentGiven, Mode=TwoWay}">
                    <CheckBox.Content>
                        <TextBlock TextWrapping="Wrap" MaxWidth="560">
                            <Run Text="Я понимаю, что перехожу на сайт стороннего инструмента с открытым кодом, использование которого может нарушать "/>
                            <Run Text="лицензионное соглашение Microsoft." FontWeight="SemiBold"/>
                            <Run Text=" Дальнейшие действия выполняю самостоятельно и принимаю за них ответственность."/>
                        </TextBlock>
                    </CheckBox.Content>
                </CheckBox>
            </Border>

            <!-- Блок активации — только для авторизованных -->
            <Grid>
                <StackPanel>
                    <!-- Управление активацией Windows -->
                    <GroupBox Header="🪟 Управление активацией Windows" Margin="0,0,0,15">
                        <StackPanel Margin="10">
                            <TextBlock Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,0,0,10">
                                <Run Text="Сторонний инструмент с открытым кодом. Откроется официальный сайт — "/>
                                <Run Text="дальнейшие действия выполняет пользователь самостоятельно."/>
                            </TextBlock>
                            <Button x:Name="btnActivateWindows" Content="🌐 Открыть официальный сайт →"
                                    Height="40" Width="260" HorizontalAlignment="Left"
                                    ToolTip="Откроет официальный сайт стороннего инструмента. Ven4Tools не запускает скрипты автоматически."
                                    FontWeight="Bold" IsEnabled="{Binding ConsentGiven}"
                                    Command="{Binding ActivateWindowsCommand}"/>
                        </StackPanel>
                    </GroupBox>

                    <!-- Управление активацией Office -->
                    <GroupBox Header="📁 Управление активацией Office" Margin="0,0,0,15">
                        <StackPanel Margin="10">
                            <TextBlock Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,0,0,10">
                                <Run Text="Сторонний инструмент с открытым кодом. Откроется официальный сайт — "/>
                                <Run Text="дальнейшие действия выполняет пользователь самостоятельно."/>
                            </TextBlock>
                            <Button x:Name="btnActivateOffice" Content="🌐 Открыть официальный сайт →"
                                    Height="40" Width="260" HorizontalAlignment="Left"
                                    ToolTip="Откроет официальный сайт стороннего инструмента. Дальнейшие действия выполняются вручную."
                                    FontWeight="Bold" IsEnabled="{Binding ConsentGiven}"
                                    Command="{Binding ActivateOfficeCommand}"/>
                        </StackPanel>
                    </GroupBox>

                </StackPanel>

            </Grid>

            <!-- Статус активации -->
            <GroupBox Header="📊 Статус активации" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <Grid Margin="0,5">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="120"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="Windows:" Foreground="{DynamicResource TextSecondary}"/>
                        <TextBlock x:Name="txtWindowsStatus" Grid.Column="1"
                                   Text="{Binding WindowsStatusText}"
                                   Foreground="{Binding WindowsStatusBrush}"/>
                    </Grid>
                    <Grid Margin="0,5">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="120"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <TextBlock Text="Office:" Foreground="{DynamicResource TextSecondary}"/>
                        <TextBlock x:Name="txtOfficeStatus" Grid.Column="1"
                                   Text="{Binding OfficeStatusText}"
                                   Foreground="{Binding OfficeStatusBrush}"/>
                    </Grid>
                    <Button x:Name="btnCheckStatus" Content="🔄 Проверить статус"
                            ToolTip="Повторно проверит состояние лицензий Windows и установленного Microsoft Office."
                            Height="32" Width="150" Margin="0,10,0,0" HorizontalAlignment="Left"
                            Command="{Binding CheckStatusCommand}"/>
                </StackPanel>
            </GroupBox>

            <!-- Информация -->
            <GroupBox Header="ℹ️ О стороннем инструменте" Margin="0,0,0,15">
                <StackPanel Margin="10">
                    <TextBlock Text="Сторонний инструмент с открытым исходным кодом для управления лицензиями."
                               Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap"/>
                    <TextBlock Text="Источник: github.com/massgravel/Microsoft-Activation-Scripts"
                               Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,5,0,0"/>
                    <TextBlock Text="⚠️ Может не поддерживать все редакции Windows. Рекомендуется Windows 10/11 Pro."
                               Foreground="#FFA500" TextWrapping="Wrap" Margin="0,10,0,0"/>
                </StackPanel>
            </GroupBox>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 2: Переписать `ActivationTab.xaml.cs`**

Полное содержимое `Ven4Tools/Views/Tabs/ActivationTab.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Активация» — тонкая обёртка над <see cref="ActivationViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, четвёртая
    /// вкладка после DebloaterTab/HistoryTab/AboutTab). Публичного контракта сверх
    /// конструктора нет.
    /// </summary>
    public partial class ActivationTab : UserControl
    {
        private readonly ActivationViewModel _viewModel = new();

        public ActivationTab()
        {
            InitializeComponent();
            DataContext = _viewModel;
            _viewModel.OwnerWindowProvider = () => Window.GetWindow(this);

            Loaded += async (_, _) =>
            {
                await _viewModel.CheckActivationStatusAsync();
            };
        }
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests`.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/Tabs/ActivationTab.xaml Ven4Tools/Views/Tabs/ActivationTab.xaml.cs
git commit -m "refactor(activation): ActivationTab — тонкая обёртка над ActivationViewModel"
```

---

### Task 3: Верификация — регрессия существующего теста

**Files:**
- Не создаёт и не меняет файлы.

**Interfaces:**
- Не применимо.

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Юнит-тесты целиком на VenchWork**

Run (на VenchWork): `dotnet test tests/Ven4Tools.Tests`
Expected: было 414/414 после AboutTab (см. память `project_ven4tools_mvvm_migration_abouttab_2026_08_25`) + 7 новых из `ActivationViewModelTests` = 421/421.

- [ ] **Step 3: Существующий UI-тест на VenchWork**

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests --filter FullyQualifiedName~Phase3RemainingTabsTests`
Expected: `ActivationTab_ПроверитьСтатус` и все остальные тесты этого класса — не хуже прежнего результата (см. соседние тесты в том же классе, HistoryTab/AboutTab уже проходили здесь).

- [ ] **Step 4: Финальный коммит верификации**

```bash
git add -A
git status
git commit -m "test(activation): MVVM-миграция ActivationTab проверена на VenchWork" --allow-empty
```

- [ ] **Step 5: Merge + push в `main`** (без дополнительного вопроса — автономная сессия)

```bash
git checkout main
git merge --ff-only mvvm-activationtab
dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental
git push origin main
git branch -d mvvm-activationtab
```

Перед пушем — обязательно проверить все коммиты ветки на `Claude-Session`-трейлер: `git log main..mvvm-activationtab --format="%B" | grep -i claude` (должно быть пусто).

---

## После задачи

Смержено и запушено в `main`. Следующая по сложности вкладка — `NetworkTab` (318 строк, один файл) — тот же процесс, новая ветка от `main`.
