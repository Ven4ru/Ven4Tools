# Обновление клиента через лаунчер — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать лаунчеру возможность обновлять установленный клиент — вручную (по кнопке) и тихо/автоматически (в фоне, без диалогов, кроме одного вопроса, если клиент запущен).

**Architecture:** Вариант А — обновление выполняет лаунчер, переиспользуя существующий пайплайн `DownloadVersionAsync` (скачивание с allow-list + SHA256 fail-closed → `SafeZipExtractor` → `TransactionalDirectoryInstaller`), единый и для ручного, и для тихого пути. Новое: диалог «закрыть клиент?» перед заменой файлов (WM_CLOSE + таймаут), защита от пересечения пути установки с папкой данных, переключатель режима в новой панели «Настройки».

**Tech Stack:** .NET 8, WPF (`Ven4Tools.Launcher`), xUnit (`tests/Ven4Tools.Tests`), FlaUI/UIA3 (`tests/Ven4Tools.UITests`).

## Global Constraints

- Полная замена каталога клиента (не патчинг) — `TransactionalDirectoryInstaller` без изменений.
- Диалог «клиент запущен, закрыть?» показывается всегда, включая тихий/автоматический режим — единственное исключение из «без вопросов».
- `Process.Kill()` никогда не форсируется — при таймауте закрытия клиента обновление отменяется с логом.
- Все тексты в UI/логах/коммитах — только на русском.
- Источник версий не меняется: GitHub Releases (истина по списку) + CDN (подстановка более быстрой ссылки для совпадающей версии) — существующий паттерн `LoadVersionsAsync`/`GitHubService`/`CdnService`.
- По умолчанию режим обновления клиента — «вручную» (`AutoUpdateClient = false`), ничего не включается без явного действия пользователя.

---

### Task 1: InstallPathGuard — защита от пересечения пути установки с папкой данных

**Files:**
- Create: `Ven4Tools.Launcher/Services/InstallPathGuard.cs`
- Test: `tests/Ven4Tools.Tests/InstallPathGuardTests.cs`

**Interfaces:**
- Produces: `internal static class InstallPathGuard { public static bool IsClientPathSafe(string clientPath, string dataFolderPath) }` — `true`, если пути не совпадают и не вложены друг в друга (case-insensitive). Используется в Task 4.

- [ ] **Step 1: Написать падающий тест**

Create `tests/Ven4Tools.Tests/InstallPathGuardTests.cs`:

```csharp
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class InstallPathGuardTests
{
    [Theory]
    [InlineData(@"C:\Ven4Tools\Ven4Tools_Client", @"C:\Users\test\AppData\Local\Ven4Tools", true)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools\Client", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4Tools", @"C:\Users\test\AppData\Local\Ven4Tools\Client", false)]
    [InlineData(@"C:\Users\test\AppData\Local\ven4tools", @"C:\Users\test\AppData\Local\Ven4Tools", false)]
    [InlineData(@"C:\Users\test\AppData\Local\Ven4ToolsExtra", @"C:\Users\test\AppData\Local\Ven4Tools", true)]
    public void IsClientPathSafe_DetectsOverlapWithDataFolder(string clientPath, string dataFolderPath, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, InstallPathGuard.IsClientPathSafe(clientPath, dataFolderPath));
    }
}
```

Последний кейс (`Ven4ToolsExtra` vs `Ven4Tools`) проверяет, что проверка не ловит ложное срабатывание на общем префиксе без разделителя пути.

- [ ] **Step 2: Запустить тест, убедиться что он падает**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~InstallPathGuardTests"`
Expected: FAIL (ошибка компиляции — `InstallPathGuard` не существует)

- [ ] **Step 3: Реализовать InstallPathGuard**

Create `Ven4Tools.Launcher/Services/InstallPathGuard.cs`:

```csharp
using System;
using System.IO;

namespace Ven4Tools.Launcher.Services
{
    // Защита от полной замены каталога клиента, если путь установки совпадает
    // с папкой локальных данных (%LOCALAPPDATA%\Ven4Tools) или вложен в неё —
    // TransactionalDirectoryInstaller удалит всё содержимое target при обновлении.
    internal static class InstallPathGuard
    {
        public static bool IsClientPathSafe(string clientPath, string dataFolderPath)
        {
            string client = Path.GetFullPath(clientPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string data = Path.GetFullPath(dataFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(client, data, StringComparison.OrdinalIgnoreCase)) return false;
            if (client.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (data.StartsWith(client + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
```

- [ ] **Step 4: Запустить тест, убедиться что он проходит**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~InstallPathGuardTests"`
Expected: PASS (6 тестов)

- [ ] **Step 5: Коммит**

```bash
git add Ven4Tools.Launcher/Services/InstallPathGuard.cs tests/Ven4Tools.Tests/InstallPathGuardTests.cs
git commit -m "Добавить защиту от пересечения пути установки клиента с папкой данных"
```

---

### Task 2: Панель «Настройки» — режим обновления клиента + перенос существующих настроек

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.xaml.cs` (поля, `LauncherSettings`)
- Modify: `Ven4Tools.Launcher/MainWindow.Settings.cs` (Load/Save, замена чекбокс-хендлеров на internal-методы)
- Modify: `Ven4Tools.Launcher/MainWindow.Tray.cs` (синхронизация чекбоксов трея с окном настроек)
- Modify: `Ven4Tools.Launcher/MainWindow.xaml` (замена Expander на кнопку)
- Create: `Ven4Tools.Launcher/SettingsWindow.xaml`
- Create: `Ven4Tools.Launcher/SettingsWindow.xaml.cs`
- Modify: `tests/Ven4Tools.UITests/LauncherSmokeTests.cs` (существующий тест ссылается на чекбоксы, которые переезжают — иначе тест сломается)

**Interfaces:**
- Produces: `MainWindow.internal void OnBackgroundUpdatesChanged(bool)`, `OnStartMinimizedChanged(bool)`, `OnAutostartChanged(bool)`, `OnAutoUpdateClientChanged(bool)` — вызываются из `SettingsWindow`. `MainWindow._autoUpdateClient` (bool) и `MainWindow._dataFolderPath` (string, `%LOCALAPPDATA%\Ven4Tools`) — используются в Task 4/6.
- Consumes: ничего нового извне.

- [ ] **Step 1: Добавить поля и модель настроек в MainWindow.xaml.cs**

В `Ven4Tools.Launcher/MainWindow.xaml.cs` заменить блок полей (строки 39-51):

```csharp
        private bool                 _backgroundUpdates = true;
        private bool                 _autostart         = false;
        private bool                 _startMinimized    = false;
```

на:

```csharp
        private bool                 _backgroundUpdates = true;
        private bool                 _autostart         = false;
        private bool                 _startMinimized    = false;
        private bool                 _autoUpdateClient  = false;
        private SettingsWindow?      _settingsWindow;
        private readonly string      _dataFolderPath;
```

Заменить блок в конструкторе (строки 65-72):

```csharp
            string appData = _isUiTestMode
                ? Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "Ven4Tools.UI.Tests")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath = Path.Combine(appData, "launcher_settings.json");
```

на:

```csharp
            string appData = _isUiTestMode
                ? Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "Ven4Tools.UI.Tests")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools");
            Directory.CreateDirectory(appData);
            _settingsPath   = Path.Combine(appData, "launcher_settings.json");
            _dataFolderPath = appData;
```

Удалить строку `SyncCheckboxes();` (была сразу после `LoadSettings();`, строка 79) — метод удаляется в Step 2, чекбоксы больше не живут на MainWindow.

Заменить класс `LauncherSettings` (строки 146-156):

```csharp
        private sealed class LauncherSettings
        {
            public bool    MinimizeToTray              { get; set; } = true;
            public string? InstallPath                 { get; set; }
            public bool    BackgroundUpdates           { get; set; } = true;
            public bool    Autostart                   { get; set; }
            public bool    StartMinimized              { get; set; }
            public string? LastNotifiedLauncherVersion { get; set; }
            public string? LastNotifiedClientVersion   { get; set; }
            public string? LastNotifiedNotificationId  { get; set; }
        }
```

на:

```csharp
        private sealed class LauncherSettings
        {
            public bool    MinimizeToTray              { get; set; } = true;
            public string? InstallPath                 { get; set; }
            public bool    BackgroundUpdates           { get; set; } = true;
            public bool    Autostart                   { get; set; }
            public bool    StartMinimized              { get; set; }
            public bool    AutoUpdateClient             { get; set; }
            public string? LastNotifiedLauncherVersion { get; set; }
            public string? LastNotifiedClientVersion   { get; set; }
            public string? LastNotifiedNotificationId  { get; set; }
        }
```

- [ ] **Step 2: Переписать MainWindow.Settings.cs**

Replace file `Ven4Tools.Launcher/MainWindow.Settings.cs` целиком:

```csharp
using System;
using System.IO;
using System.Windows;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<LauncherSettings>(json);
                    if (settings != null)
                    {
                        _minimizeToTray              = settings.MinimizeToTray;
                        _installPath                 = settings.InstallPath ?? "";
                        _backgroundUpdates           = settings.BackgroundUpdates;
                        _autostart                   = settings.Autostart;
                        _startMinimized              = settings.StartMinimized;
                        _autoUpdateClient            = settings.AutoUpdateClient;
                        _lastNotifiedLauncherVersion = settings.LastNotifiedLauncherVersion ?? "";
                        _lastNotifiedClientVersion   = settings.LastNotifiedClientVersion   ?? "";
                        _lastNotifiedNotificationId  = settings.LastNotifiedNotificationId  ?? "";
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new
                {
                    MinimizeToTray              = _minimizeToTray,
                    InstallPath                 = _installPath,
                    BackgroundUpdates           = _backgroundUpdates,
                    Autostart                   = _autostart,
                    StartMinimized              = _startMinimized,
                    AutoUpdateClient            = _autoUpdateClient,
                    LastNotifiedLauncherVersion = _lastNotifiedLauncherVersion,
                    LastNotifiedClientVersion   = _lastNotifiedClientVersion,
                    LastNotifiedNotificationId  = _lastNotifiedNotificationId
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                // Атомарная запись: сначала во временный файл, затем замена.
                // Так настройки не побьются при сбое в момент записи.
                string tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_settingsPath))
                    File.Replace(tmp, _settingsPath, null);
                else
                    File.Move(tmp, _settingsPath);
            }
            catch { }
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(
                    this, _backgroundUpdates, _startMinimized, _autostart, _autoUpdateClient)
                {
                    Owner = this
                };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // Значения могут поменяться из контекстного меню трея, пока окно настроек
        // открыто — держим его в курсе.
        private void SyncSettingsWindow() =>
            _settingsWindow?.Sync(_backgroundUpdates, _startMinimized, _autostart, _autoUpdateClient);

        internal void OnBackgroundUpdatesChanged(bool isChecked)
        {
            _backgroundUpdates = isChecked;
            if (_trayItemBgUpdates != null) _trayItemBgUpdates.Checked = _backgroundUpdates;
            if (_backgroundUpdates)
                _updateService?.Start();
            else
                _updateService?.Stop();
            SaveSettings();
        }

        internal void OnStartMinimizedChanged(bool isChecked)
        {
            _startMinimized = isChecked;
            SaveSettings();
        }

        internal void OnAutostartChanged(bool isChecked)
        {
            _autostart = isChecked;
            if (_trayItemAutostart != null) _trayItemAutostart.Checked = _autostart;
            if (!_isUiTestMode)
                SetAutostart(_autostart);
            SaveSettings();
        }

        internal void OnAutoUpdateClientChanged(bool isChecked)
        {
            _autoUpdateClient = isChecked;
            SaveSettings();
        }

        private static bool GetAutostart()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("Ven4Tools.Launcher") != null;
            }
            catch { return false; }
        }

        private static void SetAutostart(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;

                if (enable)
                {
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(exe)) return; // single-file publish — MainModule может быть null
                    key.SetValue("Ven4Tools.Launcher", $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue("Ven4Tools.Launcher", throwOnMissingValue: false);
                }
            }
            catch { }
        }
    }
}
```

- [ ] **Step 3: Заменить прямые обращения к чекбоксам в MainWindow.Tray.cs**

В `Ven4Tools.Launcher/MainWindow.Tray.cs` заменить:

```csharp
                _trayItemAutostart.CheckedChanged += (s, e) =>
                {
                    _autostart = _trayItemAutostart.Checked;
                    SetAutostart(_autostart);
                    SaveSettings();
                    Dispatcher.Invoke(() => chkAutostart.IsChecked = _autostart);
                };
```

на:

```csharp
                _trayItemAutostart.CheckedChanged += (s, e) =>
                {
                    _autostart = _trayItemAutostart.Checked;
                    SetAutostart(_autostart);
                    SaveSettings();
                    Dispatcher.Invoke(SyncSettingsWindow);
                };
```

и заменить:

```csharp
                _trayItemBgUpdates.CheckedChanged += (s, e) =>
                {
                    _backgroundUpdates = _trayItemBgUpdates.Checked;
                    if (_backgroundUpdates)
                        _updateService?.Start();
                    else
                        _updateService?.Stop();
                    SaveSettings();
                    Dispatcher.Invoke(() => chkBackgroundUpdates.IsChecked = _backgroundUpdates);
                };
```

на:

```csharp
                _trayItemBgUpdates.CheckedChanged += (s, e) =>
                {
                    _backgroundUpdates = _trayItemBgUpdates.Checked;
                    if (_backgroundUpdates)
                        _updateService?.Start();
                    else
                        _updateService?.Stop();
                    SaveSettings();
                    Dispatcher.Invoke(SyncSettingsWindow);
                };
```

- [ ] **Step 4: Заменить Expander на кнопку в MainWindow.xaml**

В `Ven4Tools.Launcher/MainWindow.xaml` заменить блок:

```xml
                        <Expander Header="Поведение лаунчера" IsExpanded="True" Margin="0,0,0,8">
                            <StackPanel Margin="2,8,0,4">
                                <CheckBox x:Name="chkBackgroundUpdates" Margin="0,4" Click="ChkBackgroundUpdates_Click">
                                    <TextBlock Text="Проверять обновления в фоне" TextWrapping="Wrap" MaxWidth="170"/>
                                </CheckBox>
                                <CheckBox x:Name="chkStartMinimized" Margin="0,4" Click="ChkStartMinimized_Click">
                                    <TextBlock Text="Запускать свёрнутым в трей" TextWrapping="Wrap" MaxWidth="170"/>
                                </CheckBox>
                                <CheckBox x:Name="chkAutostart" Margin="0,4" Click="ChkAutostart_Click">
                                    <TextBlock Text="Запускать при старте Windows" TextWrapping="Wrap" MaxWidth="170"/>
                                </CheckBox>
                            </StackPanel>
                        </Expander>
```

на:

```xml
                        <Button x:Name="btnOpenSettings" Content="⚙ Настройки"
                                Style="{StaticResource SideCommand}" Click="BtnOpenSettings_Click" Margin="0,0,0,8"/>
```

- [ ] **Step 5: Создать SettingsWindow.xaml**

Create `Ven4Tools.Launcher/SettingsWindow.xaml`:

```xml
<Window x:Class="Ven4Tools.Launcher.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Настройки — Ven4Tools Launcher" Height="380" Width="380"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="{DynamicResource WindowBackground}"
        FontFamily="Segoe UI Variable Text, Segoe UI">
    <StackPanel Margin="20">
        <TextBlock Text="ПОВЕДЕНИЕ ЛАУНЧЕРА" Style="{StaticResource EyebrowStyle}" Margin="0,0,0,8"/>
        <CheckBox x:Name="chkBackgroundUpdates" Margin="0,4" Click="ChkBackgroundUpdates_Click">
            <TextBlock Text="Проверять обновления в фоне" TextWrapping="Wrap"/>
        </CheckBox>
        <CheckBox x:Name="chkStartMinimized" Margin="0,4" Click="ChkStartMinimized_Click">
            <TextBlock Text="Запускать свёрнутым в трей" TextWrapping="Wrap"/>
        </CheckBox>
        <CheckBox x:Name="chkAutostart" Margin="0,4" Click="ChkAutostart_Click">
            <TextBlock Text="Запускать при старте Windows" TextWrapping="Wrap"/>
        </CheckBox>

        <TextBlock Text="ОБНОВЛЕНИЕ КЛИЕНТА" Style="{StaticResource EyebrowStyle}" Margin="0,20,0,8"/>
        <RadioButton x:Name="rbAutoUpdateManual" GroupName="AutoUpdateMode" Margin="0,4"
                     Foreground="{DynamicResource TextPrimary}" Click="RbAutoUpdateMode_Click">
            <TextBlock Text="Вручную"/>
        </RadioButton>
        <RadioButton x:Name="rbAutoUpdateAuto" GroupName="AutoUpdateMode" Margin="0,4"
                     Foreground="{DynamicResource TextPrimary}" Click="RbAutoUpdateMode_Click">
            <TextBlock Text="Автоматически"/>
        </RadioButton>

        <Button x:Name="btnCloseSettings" Content="Закрыть" Height="34" Margin="0,24,0,0"
                HorizontalAlignment="Right" Width="100" Click="BtnClose_Click"/>
    </StackPanel>
</Window>
```

- [ ] **Step 6: Создать SettingsWindow.xaml.cs**

Create `Ven4Tools.Launcher/SettingsWindow.xaml.cs`:

```csharp
using System.Windows;

namespace Ven4Tools.Launcher
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _owner;

        public SettingsWindow(MainWindow owner, bool backgroundUpdates, bool startMinimized,
            bool autostart, bool autoUpdateClient)
        {
            InitializeComponent();
            _owner = owner;
            Sync(backgroundUpdates, startMinimized, autostart, autoUpdateClient);
        }

        // Programmatic IsChecked assignment does not raise Click — безопасно
        // вызывать в любой момент, не вызовет каскад Save.
        internal void Sync(bool backgroundUpdates, bool startMinimized, bool autostart, bool autoUpdateClient)
        {
            chkBackgroundUpdates.IsChecked = backgroundUpdates;
            chkStartMinimized.IsChecked    = startMinimized;
            chkAutostart.IsChecked         = autostart;
            rbAutoUpdateManual.IsChecked   = !autoUpdateClient;
            rbAutoUpdateAuto.IsChecked     = autoUpdateClient;
        }

        private void ChkBackgroundUpdates_Click(object sender, RoutedEventArgs e) =>
            _owner.OnBackgroundUpdatesChanged(chkBackgroundUpdates.IsChecked == true);

        private void ChkStartMinimized_Click(object sender, RoutedEventArgs e) =>
            _owner.OnStartMinimizedChanged(chkStartMinimized.IsChecked == true);

        private void ChkAutostart_Click(object sender, RoutedEventArgs e) =>
            _owner.OnAutostartChanged(chkAutostart.IsChecked == true);

        private void RbAutoUpdateMode_Click(object sender, RoutedEventArgs e) =>
            _owner.OnAutoUpdateClientChanged(rbAutoUpdateAuto.IsChecked == true);

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
```

- [ ] **Step 7: Собрать решение**

Run: `dotnet build Ven4Tools.Launcher -c Release`
Expected: Build succeeded, 0 Error(s)

- [ ] **Step 8: Обновить LauncherSmokeTests.cs — чекбоксы больше не на главном окне**

В `tests/Ven4Tools.UITests/LauncherSmokeTests.cs` заменить в `AssertPrimaryControlsAreAvailable()`:

```csharp
        string[] requiredEnabledControls =
        [
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnChangelog",
            "btnDeleteClient",
            "btnExit",
            "chkBackgroundUpdates",
            "chkStartMinimized",
            "chkAutostart"
        ];
```

на:

```csharp
        string[] requiredEnabledControls =
        [
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnChangelog",
            "btnOpenSettings",
            "btnDeleteClient",
            "btnExit"
        ];
```

Заменить в `ExercisePrimaryControlBindings()` блок:

```csharp
        foreach (string automationId in new[]
        {
            "chkBackgroundUpdates",
            "chkStartMinimized",
            "chkAutostart"
        })
        {
            CheckBox checkBox = _window.FindFirstDescendant(
                    condition => condition.ByAutomationId(automationId))!
                .AsCheckBox();
            ToggleState initialState = checkBox.ToggleState;
            checkBox.Toggle();
            checkBox.Toggle();
            Assert.Equal(initialState, checkBox.ToggleState);
        }
    }
```

на:

```csharp
        ExerciseSettingsWindow();
    }

    private void ExerciseSettingsWindow()
    {
        _window.FindFirstDescendant(condition => condition.ByAutomationId("btnOpenSettings"))!
            .AsButton()
            .Invoke();

        Window settingsWindow = Retry.WhileNull(
            () => _application.GetAllTopLevelWindows(_automation)
                .FirstOrDefault(w => w.Title.Contains("Настройки", StringComparison.OrdinalIgnoreCase)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(250)).Result
            ?? throw new InvalidOperationException("Окно «Настройки» не открылось.");

        foreach (string automationId in new[]
        {
            "chkBackgroundUpdates",
            "chkStartMinimized",
            "chkAutostart"
        })
        {
            CheckBox checkBox = settingsWindow.FindFirstDescendant(
                    condition => condition.ByAutomationId(automationId))!
                .AsCheckBox();
            ToggleState initialState = checkBox.ToggleState;
            checkBox.Toggle();
            checkBox.Toggle();
            Assert.Equal(initialState, checkBox.ToggleState);
        }

        // Переключатель режима обновления клиента переживает переключение туда-обратно.
        RadioButton manual = settingsWindow.FindFirstDescendant(
                condition => condition.ByAutomationId("rbAutoUpdateManual"))!
            .AsRadioButton();
        RadioButton auto = settingsWindow.FindFirstDescendant(
                condition => condition.ByAutomationId("rbAutoUpdateAuto"))!
            .AsRadioButton();
        bool wasManual = manual.IsChecked == true;
        auto.Click();
        Assert.True(auto.IsChecked);
        manual.Click();
        Assert.True(manual.IsChecked);
        if (!wasManual) auto.Click();

        settingsWindow.FindFirstDescendant(condition => condition.ByAutomationId("btnCloseSettings"))!
            .AsButton()
            .Invoke();
    }
```

Добавить `using System.Linq;` в блок `using` в начале файла (если отсутствует — сейчас файл его не использует явно, но `FirstOrDefault` в новом коде требует).

- [ ] **Step 9: Пересобрать Release и прогнать UI-тест лаунчера**

Run:
```bash
dotnet build Ven4Tools.sln -c Release
dotnet test tests/Ven4Tools.UITests --filter "FullyQualifiedName~LauncherSmokeTests"
```
Expected: PASS (снапшот UI может потребовать `UPDATE_SNAPSHOTS=1` из-за изменённого сайдбара — см. Task 2 Step 10)

- [ ] **Step 10: Обновить визуальный снапшот лаунчера**

Сайдбар физически изменился (Expander → кнопка) — эталон `tests/Ven4Tools.UITests/Snapshots/launcher-main.png` больше не совпадает. Перегенерировать на чистом рабочем столе (без посторонних окон поверх — см. известное ограничение в памяти проекта):

```bash
UPDATE_SNAPSHOTS=1 dotnet test tests/Ven4Tools.UITests --filter "FullyQualifiedName~LauncherSmokeTests"
git diff --stat tests/Ven4Tools.UITests/Snapshots/launcher-main.png
```

Проверить сохранённый PNG визуально перед коммитом — захват должен показывать окно лаунчера, а не постороннее окно.

- [ ] **Step 11: Коммит**

```bash
git add Ven4Tools.Launcher/MainWindow.xaml.cs Ven4Tools.Launcher/MainWindow.Settings.cs \
        Ven4Tools.Launcher/MainWindow.Tray.cs Ven4Tools.Launcher/MainWindow.xaml \
        Ven4Tools.Launcher/SettingsWindow.xaml Ven4Tools.Launcher/SettingsWindow.xaml.cs \
        tests/Ven4Tools.UITests/LauncherSmokeTests.cs tests/Ven4Tools.UITests/Snapshots/launcher-main.png
git commit -m "Вынести настройки лаунчера в отдельную панель, добавить режим обновления клиента"
```

---

### Task 3: Штатное закрытие запущенного клиента (WM_CLOSE + таймаут)

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Components.cs`

**Interfaces:**
- Consumes: ничего нового.
- Produces: `MainWindow.private Task<bool> TryCloseRunningClientAsync(int timeoutMs = 10000)` — используется в Task 4. `MainWindow.private Process? FindRunningClientProcess()` — используется внутри `IsClientRunning()` (без изменения его сигнатуры/поведения для существующих вызывающих).

- [ ] **Step 1: Рефакторинг IsClientRunning + новые методы**

В `Ven4Tools.Launcher/MainWindow.Components.cs` заменить метод (строки 534-565):

```csharp
        // Запущен ли клиент Ven4Tools из текущей папки установки.
        // Если MainModule недоступен — считаем запущенным: безопаснее показать
        // предупреждение лишний раз, чем оставить папку клиента в битом состоянии.
        private bool IsClientRunning()
        {
            try
            {
                string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
                var processes = Process.GetProcessesByName("Ven4Tools");
                try
                {
                    foreach (var proc in processes)
                    {
                        try
                        {
                            string? exePath = proc.MainModule?.FileName;
                            if (string.IsNullOrEmpty(exePath)) return true;
                            if (string.Equals(exePath, clientExe, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        catch { return true; }
                    }
                }
                finally
                {
                    // Диспоузим все найденные процессы, а не только тот, на котором
                    // остановился цикл — иначе хэндлы "хвоста" массива держатся до GC.
                    foreach (var proc in processes) proc.Dispose();
                }
            }
            catch { }
            return false;
        }
```

на:

```csharp
        // Запущен ли клиент Ven4Tools из текущей папки установки.
        private bool IsClientRunning()
        {
            var proc = FindRunningClientProcess();
            proc?.Dispose();
            return proc != null;
        }

        // Находит процесс запущенного клиента из текущей папки установки и возвращает
        // его НЕ освобождённым — вызывающий (TryCloseRunningClientAsync) сам вызывает
        // Dispose(). Остальные (непарные) найденные процессы освобождаются здесь же.
        // Если MainModule недоступен — считаем процесс совпадением: безопаснее показать
        // предупреждение лишний раз, чем оставить папку клиента в битом состоянии.
        private Process? FindRunningClientProcess()
        {
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
            Process[] processes;
            try { processes = Process.GetProcessesByName("Ven4Tools"); }
            catch { return null; }

            foreach (var proc in processes)
            {
                bool isMatch;
                try
                {
                    string? exePath = proc.MainModule?.FileName;
                    isMatch = string.IsNullOrEmpty(exePath) ||
                              string.Equals(exePath, clientExe, StringComparison.OrdinalIgnoreCase);
                }
                catch { isMatch = true; }

                if (isMatch)
                {
                    foreach (var other in processes) if (other != proc) other.Dispose();
                    return proc;
                }
                proc.Dispose();
            }
            return null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010;

        // Просит запущенный клиент закрыться штатно (WM_CLOSE — то же сообщение,
        // что шлёт крестик окна) и ждёт до timeoutMs, пока процесс завершится.
        // Клиент сам решает, закрываться ли (см. Window_Closing_Extended в
        // Ven4Tools/MainWindow.xaml.cs — предупреждение при активной установке,
        // либо сворачивание в трей вместо закрытия при включённой у клиента
        // соответствующей настройке — тогда процесс не завершится, и этот метод
        // вернёт false по таймауту; форсированный Process.Kill() не используется).
        private async Task<bool> TryCloseRunningClientAsync(int timeoutMs = 10000)
        {
            var proc = FindRunningClientProcess();
            if (proc == null) return true;

            IntPtr handle = proc.MainWindowHandle;
            proc.Dispose();

            if (handle == IntPtr.Zero)
            {
                AddLog("⚠️ Не найдено окно клиента для закрытия (возможно, уже свёрнут в трей)");
                return false;
            }

            PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsClientRunning()) return true;
                await Task.Delay(500);
            }
            return false;
        }
```

- [ ] **Step 2: Собрать решение**

Run: `dotnet build Ven4Tools.Launcher -c Release`
Expected: Build succeeded, 0 Error(s), 0 Warning(s)

- [ ] **Step 3: Коммит**

```bash
git add Ven4Tools.Launcher/MainWindow.Components.cs
git commit -m "Добавить штатное закрытие запущенного клиента через WM_CLOSE перед обновлением"
```

---

### Task 4: DownloadVersionAsync — диалог закрытия клиента + защита пути + тихий режим

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Download.cs`

**Interfaces:**
- Consumes: `InstallPathGuard.IsClientPathSafe(string, string)` (Task 1), `TryCloseRunningClientAsync()` (Task 3), `MainWindow._dataFolderPath` (Task 2).
- Produces: `MainWindow.private Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token, bool silent = false)` — новый параметр `silent`, используется в Task 5/6.

- [ ] **Step 1: Изменить сигнатуру и обработку «клиент запущен» + защиту пути**

В `Ven4Tools.Launcher/MainWindow.Download.cs` заменить сигнатуру:

```csharp
        private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token)
        {
```

на:

```csharp
        private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token, bool silent = false)
        {
```

Заменить блок:

```csharp
                // Нельзя перезаписывать файлы запущенного клиента.
                if (IsClientRunning())
                {
                    txtDownloadStatus.Text = "Клиент запущен";
                    AddLog("⚠️ Ven4Tools запущен — закройте клиент перед обновлением");
                    System.Windows.MessageBox.Show(
                        "Ven4Tools сейчас запущен.\n\nЗакройте приложение и повторите установку обновления.",
                        "Клиент запущен", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                txtDownloadStatus.Text = "Установка файлов...";
                var installer = new TransactionalDirectoryInstaller();
                installer.Install(extractPath, _clientPath, token);
```

на:

```csharp
                // Нельзя перезаписывать файлы запущенного клиента — спрашиваем и просим
                // закрыться штатно. Диалог показывается всегда, даже в тихом
                // автоматическом режиме — единственное исключение из «без вопросов».
                if (IsClientRunning())
                {
                    txtDownloadStatus.Text = "Клиент запущен";
                    var answer = System.Windows.MessageBox.Show(
                        "Ven4Tools сейчас запущен.\n\nЗакрыть клиент сейчас, чтобы установить обновление?",
                        "Клиент запущен", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (answer != MessageBoxResult.Yes)
                    {
                        AddLog("⏹ Обновление отменено — клиент не закрыт");
                        return;
                    }

                    AddLog("🔒 Закрываю клиент перед установкой обновления...");
                    if (!await TryCloseRunningClientAsync())
                    {
                        txtDownloadStatus.Text = "Клиент запущен";
                        AddLog("⚠️ Клиент не закрылся за отведённое время — обновление отменено");
                        System.Windows.MessageBox.Show(
                            "Не удалось закрыть клиент автоматически (возможно, он свёрнут в трей).\n\n" +
                            "Закройте его вручную и повторите установку обновления.",
                            "Клиент не закрылся", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    AddLog("✅ Клиент закрыт, продолжаю установку");
                }

                if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
                {
                    txtDownloadStatus.Text = "Ошибка пути";
                    AddLog($"⛔ Папка установки клиента пересекается с папкой данных — обновление отменено: {_clientPath}");
                    if (!silent)
                        System.Windows.MessageBox.Show(
                            $"Папка установки клиента:\n{_clientPath}\n\nсовпадает или вложена в папку данных Ven4Tools. " +
                            "Обновление отменено во избежание потери настроек.\n\nВыберите другую папку установки.",
                            "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                txtDownloadStatus.Text = "Установка файлов...";
                var installer = new TransactionalDirectoryInstaller();
                installer.Install(extractPath, _clientPath, token);
```

- [ ] **Step 2: Подавить диалоги успеха/ошибки в тихом режиме**

Заменить:

```csharp
                System.Windows.MessageBox.Show(
                    $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
                    "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
```

на:

```csharp
                if (!silent)
                    System.Windows.MessageBox.Show(
                        $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
```

Заменить:

```csharp
            catch (Exception ex)
            {
                txtDownloadStatus.Text = "Ошибка";
                AddLog($"❌ Ошибка скачивания: {ex.Message}");
                System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
```

на:

```csharp
            catch (Exception ex)
            {
                txtDownloadStatus.Text = "Ошибка";
                AddLog($"❌ Ошибка скачивания: {ex.Message}");
                if (!silent)
                    System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
```

- [ ] **Step 3: Собрать решение**

Run: `dotnet build Ven4Tools.Launcher -c Release`
Expected: Build succeeded, 0 Error(s)

- [ ] **Step 4: Коммит**

```bash
git add Ven4Tools.Launcher/MainWindow.Download.cs
git commit -m "Обновление клиента: диалог закрытия запущенного клиента, защита пути, тихий режим"
```

---

### Task 5: Обнаружение обновления клиента — «Проверить обновления» + состояние btnLaunchApp

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Versions.cs`
- Modify: `Ven4Tools.Launcher/MainWindow.Download.cs`
- Modify: `Ven4Tools.Launcher/MainWindow.xaml.cs` (новое поле)

**Interfaces:**
- Consumes: `DownloadVersionAsync` (Task 4).
- Produces: `MainWindow._clientUpdateAvailable` (bool) — читается в Task 6 косвенно через `_selectedVersion`.

- [ ] **Step 1: Добавить поле состояния**

В `Ven4Tools.Launcher/MainWindow.xaml.cs` добавить рядом с `_selectedVersion` (строка 34):

```csharp
        private ClientVersionInfo?   _selectedVersion;
        private bool                 _clientUpdateAvailable = false;
```

- [ ] **Step 2: Добавить CheckClientUpdateAvailable и вызвать его из LoadVersionsAsync**

В `Ven4Tools.Launcher/MainWindow.Versions.cs` заменить:

```csharp
                if (_availableVersions.Any())
                {
                    cmbVersions.ItemsSource  = _availableVersions;
                    cmbVersions.SelectedItem = _availableVersions.FirstOrDefault(v => v.IsLatest);
                    cmbVersions.IsEnabled    = true;
                    AddLog($"✅ Загружено {_availableVersions.Count} версий");
                    CheckExistingClient();
                }
```

на:

```csharp
                if (_availableVersions.Any())
                {
                    cmbVersions.ItemsSource  = _availableVersions;
                    cmbVersions.SelectedItem = _availableVersions.FirstOrDefault(v => v.IsLatest);
                    cmbVersions.IsEnabled    = true;
                    AddLog($"✅ Загружено {_availableVersions.Count} версий");
                    CheckExistingClient();
                    CheckClientUpdateAvailable();
                }
```

Добавить новый метод сразу после `CheckExistingClient()`:

```csharp
        // Сравнивает установленную версию клиента с последней доступной и переключает
        // btnLaunchApp в состояние «Обновить», если найдена более новая версия.
        // Вызывается после LoadVersionsAsync — общий путь и для ручной проверки
        // («Проверить обновления»), и для авто-обновления (Task 6).
        private void CheckClientUpdateAvailable()
        {
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
            if (!File.Exists(clientExe)) { _clientUpdateAvailable = false; return; }

            string installedVersion = FileVersionInfo.GetVersionInfo(clientExe).FileVersion ?? "0.0.0";
            var latest = _availableVersions.FirstOrDefault(v => v.IsLatest);
            if (latest == null || !VersionComparer.IsNewer(latest.Version, installedVersion))
            {
                _clientUpdateAvailable = false;
                return;
            }

            _clientUpdateAvailable  = true;
            _selectedVersion        = latest;
            cmbVersions.SelectedItem = latest;
            btnLaunchApp.Content    = "⬆ Обновить Ven4Tools";
            btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
            AddLog($"📢 Доступно обновление клиента: {installedVersion} → {latest.Version}");
        }
```

- [ ] **Step 3: Обработать состояние «Обновить» в BtnLaunchApp_Click**

В `Ven4Tools.Launcher/MainWindow.Download.cs` заменить начало метода:

```csharp
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");

            if (File.Exists(clientExe))
            {
                AddLog($"🚀 Запуск Ven4Tools {_selectedVersion.Version}...");
```

на:

```csharp
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");

            if (File.Exists(clientExe) && _clientUpdateAvailable)
            {
                AddLog($"⬆ Обновление клиента до {_selectedVersion.Version}...");
                _clientUpdateAvailable = false;
                _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await DownloadVersionAsync(_selectedVersion, _downloadCts.Token);
                return;
            }

            if (File.Exists(clientExe))
            {
                AddLog($"🚀 Запуск Ven4Tools {_selectedVersion.Version}...");
```

- [ ] **Step 4: Собрать решение**

Run: `dotnet build Ven4Tools.Launcher -c Release`
Expected: Build succeeded, 0 Error(s)

- [ ] **Step 5: Ручная проверка**

Запустить лаунчер (elevated, `requireAdministrator` у клиента не касается лаунчера напрямую, но установка клиента внутри может требовать прав — использовать обычный запуск), установить более старую версию клиента через `cmbVersions` (если доступна в списке релизов), нажать «Проверить обновления» — убедиться, что `btnLaunchApp` переключается на «⬆ Обновить Ven4Tools» и клик действительно скачивает и ставит более новую версию.

- [ ] **Step 6: Коммит**

```bash
git add Ven4Tools.Launcher/MainWindow.Versions.cs Ven4Tools.Launcher/MainWindow.Download.cs Ven4Tools.Launcher/MainWindow.xaml.cs
git commit -m "«Проверить обновления» теперь сразу проверяет и клиент, кнопка запуска переключается в режим «Обновить»"
```

---

### Task 6: Тихое автоматическое обновление клиента

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Tray.cs`

**Interfaces:**
- Consumes: `MainWindow._autoUpdateClient` (Task 2), `DownloadVersionAsync(..., silent: true)` (Task 4), `LoadVersionsAsync()` (Task 5), `MainWindow._downloadCts`.

- [ ] **Step 1: Добавить триггер тихого обновления в OnUpdateAvailable**

В `Ven4Tools.Launcher/MainWindow.Tray.cs` добавить `using System.Linq;` в блок `using` в начале файла.

Заменить:

```csharp
                    if (type == "launcher")
                        btnInstallUpdate.Visibility = Visibility.Visible;
                });
            }
            catch { } // Dispatcher может быть выключен при завершении приложения
        }
```

на:

```csharp
                    if (type == "launcher")
                        btnInstallUpdate.Visibility = Visibility.Visible;
                    else
                        _ = TriggerAutoClientUpdateAsync(info.LatestVersion ?? "");
                });
            }
            catch { } // Dispatcher может быть выключен при завершении приложения
        }

        // Тихое обновление клиента при включённом автоматическом режиме. Список версий
        // на момент фонового обнаружения (UpdateBackgroundService.CheckClientAsync)
        // мог не содержать CDN-подстановки — перезагружаем тем же путём, что и ручная
        // проверка, чтобы получить актуальный ClientVersionInfo с FallbackUrl/ExpectedSha256.
        private async Task TriggerAutoClientUpdateAsync(string latestVersion)
        {
            if (!_autoUpdateClient) return;
            if (_downloadCts != null) return; // уже идёт другая загрузка — попробуем на следующем тике

            await LoadVersionsAsync();
            var match = _availableVersions.FirstOrDefault(v => v.Version == latestVersion);
            if (match == null)
            {
                AddLog($"⚠️ Автообновление: версия {latestVersion} не найдена в свежем списке — пропуск");
                return;
            }

            AddLog($"🤖 Автоматическое обновление клиента до {latestVersion}...");
            _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await DownloadVersionAsync(match, _downloadCts.Token, silent: true);
        }
```

- [ ] **Step 2: Собрать решение**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: Build succeeded, 0 Error(s), 0 Warning(s)

- [ ] **Step 3: Прогнать полный юнит-тестовый набор**

Run: `dotnet test tests/Ven4Tools.Tests`
Expected: PASS, без регрессий

- [ ] **Step 4: Ручная проверка тихого пути**

Включить «Автоматически» в панели «Настройки», убедиться (по логу лаунчера), что при появлении новой версии в GitHub Releases обновление клиента проходит без диалогов, если клиент не запущен, и с одним диалогом «закрыть клиент?», если запущен.

- [ ] **Step 5: Коммит**

```bash
git add Ven4Tools.Launcher/MainWindow.Tray.cs
git commit -m "Добавить тихое автоматическое обновление клиента в фоновом режиме"
```

---

## Self-Review (проведён при написании плана)

**1. Покрытие спеки:** Механизм обновления (Task 4), путь/данные (Task 1, интегрирован в Task 4), диалог «клиент запущен» (Task 3+4), панель «Настройки» с режимом (Task 2), ручная кнопка проверки (Task 5 — расширяет существующую `btnCheckUpdates`, не создаёт новую, как и решено в дизайне), тихий фон (Task 6). Тестирование по разделу спеки: unit для новой чистой логики (Task 1), FlaUI для UI (Task 2 Step 8-10), реальный прогон — вручную (Task 5 Step 5, Task 6 Step 4), как и предписано риск-классификацией.

**2. Плейсхолдеры:** отсутствуют — весь код в шагах полный и вставляемый как есть.

**3. Согласованность типов:** `DownloadVersionAsync(ClientVersionInfo version, CancellationToken token, bool silent = false)` — сигнатура одинакова в Task 4 (определение) и Task 5/6 (вызовы). `InstallPathGuard.IsClientPathSafe(string, string)` — одинаково в Task 1 (определение/тест) и Task 4 (вызов). `TryCloseRunningClientAsync(int timeoutMs = 10000)` — определён в Task 3, вызывается без аргументов в Task 4 (использует значение по умолчанию). `_clientUpdateAvailable`, `_autoUpdateClient`, `_dataFolderPath`, `_settingsWindow` — объявлены один раз каждое (Task 5/2/2/2 соответственно), используются в последующих задачах без повторного объявления.
