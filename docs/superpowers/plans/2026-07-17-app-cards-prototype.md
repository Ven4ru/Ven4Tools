# Карточка приложения (прототип) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Клик по названию приложения в каталоге открывает модальное окно-карточку (Steam-style, вариант B) с описанием, ссылкой на сайт, кнопками «Запустить/Установить», «Переустановить», «Удалить», «О программе».

**Architecture:** Новый `AppCardViewModel` оборачивает существующий `AppRowViewModel` (переиспользует `LaunchCommand`, `IsInstalled`, `CanLaunch`), не дублируя состояние. Новое модальное окно `AppCardWindow` открывается из `CatalogViewModel` по команде `OpenCardCommand`, тем же способом, каким уже открываются `AlternativeSourceDialog`/`PresetSaveDialog` (через `OwnerWindowProvider`). Уборка (uninstall) выносится из `InstalledTab.xaml.cs` в переиспользуемый `AppUninstallService`, чтобы карточка и вкладка «Установленные» не дублировали код.

**Tech Stack:** WPF/.NET 8, MVVM (существующие `RelayCommand`/`INotifyPropertyChanged`), xUnit для тестов.

## Global Constraints

- Прототип — изолированный репозиторий `Ven4Tools-cards-prototype` (клон без remote), НЕ пушить, НЕ переносить в основной `Ven4Tools` без отдельного решения пользователя.
- Никаких новых полей в серверный каталог (`master.json`) — только доведение уже существующих полей (`description`, `version`, `size`) до модели клиента, которая их сейчас молча игнорирует.
- Все тексты в UI — на русском.
- Не добавлять реальные баннеры/скриншоты — баннер карточки = растянутая/размытая существующая `iconUrl`.
- Сборка: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release` → 0 ошибок, 0 предупреждений после каждой задачи.

---

### Task 1: Прокинуть Description/Version/Size из каталога в строку каталога

**Files:**
- Modify: `Ven4Tools/Models/App.cs`
- Modify: `Ven4Tools/ViewModels/AppRowViewModel.cs`
- Modify: `Ven4Tools/ViewModels/CatalogViewModel.cs:530-534`
- Test: `tests/Ven4Tools.Tests/CatalogAppFieldsTests.cs`

**Interfaces:**
- Produces: `App.Description` (string), `App.Version` (string) — новые поля модели каталога. `AppRowViewModel.Description` (string?), `AppRowViewModel.CatalogVersion` (string?), `AppRowViewModel.CatalogSizeText` (string?) — читаются в Task 3.

- [ ] **Step 1: Написать падающий тест на десериализацию Description/Version**

Создать `tests/Ven4Tools.Tests/CatalogAppFieldsTests.cs`:

```csharp
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Tests;

public sealed class CatalogAppFieldsTests
{
    [Fact]
    public void App_DeserializesDescriptionVersionSize_FromCatalogJson()
    {
        const string json = """
        {
          "id": "firefox",
          "name": "Mozilla Firefox",
          "category": "Браузеры",
          "wingetId": "Mozilla.Firefox",
          "downloadUrl": "https://download.mozilla.org/?product=firefox-latest",
          "version": "152.0.4",
          "size": "84.7 MB",
          "iconUrl": "https://cdn.simpleicons.org/firefox",
          "description": "Быстрый, безопасный браузер."
        }
        """;

        var app = JsonConvert.DeserializeObject<App>(json)!;

        Assert.Equal("152.0.4", app.Version);
        Assert.Equal("84.7 MB", app.Size);
        Assert.Equal("Быстрый, безопасный браузер.", app.Description);
    }
}
```

- [ ] **Step 2: Запустить тест, убедиться что падает**

Run: `dotnet test tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj -c Release --filter CatalogAppFieldsTests`
Expected: FAIL — `Assert.Equal("152.0.4", app.Version)` не проходит, т.к. `App.Version` возвращает `""` (свойства ещё нет, Newtonsoft молча игнорирует незнакомые JSON-ключи `version`/`description`).

- [ ] **Step 3: Добавить поля в модель каталога**

В `Ven4Tools/Models/App.cs`, после существующего `public string IconUrl { get; set; } = string.Empty;` (строка 20) добавить:

```csharp
        public string Description { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;
```

- [ ] **Step 4: Запустить тест, убедиться что проходит**

Run: `dotnet test tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj -c Release --filter CatalogAppFieldsTests`
Expected: PASS

- [ ] **Step 5: Прокинуть поля на строку каталога (AppRowViewModel)**

В `Ven4Tools/ViewModels/AppRowViewModel.cs`, сразу после `public string? IconUrl { get; set; }` добавить:

```csharp
        // Описание/версия/размер из каталога (master.json) — раньше эти поля
        // JSON молча игнорировались (AppInfo их не содержит), для карточки
        // приложения нужны отдельно от IconUrl тем же способом.
        public string? Description { get; set; }

        // Версия из каталога (последняя доступная) — отличается от InstalledVersion
        // (реально установленной), нужна для карточки, когда приложение ещё не
        // установлено.
        public string? CatalogVersion { get; set; }

        public string? CatalogSizeText { get; set; }
```

- [ ] **Step 6: Заполнить поля при построении строк каталога**

В `Ven4Tools/ViewModels/CatalogViewModel.cs`, метод `BuildRows()`, заменить:

```csharp
                    var row = new AppRowViewModel(appInfo)
                    {
                        IconUrl = catalogApp.IconUrl,
                        Profile = catalogApp.Profile
                    };
```

на:

```csharp
                    var row = new AppRowViewModel(appInfo)
                    {
                        IconUrl = catalogApp.IconUrl,
                        Profile = catalogApp.Profile,
                        Description = catalogApp.Description,
                        CatalogVersion = catalogApp.Version,
                        CatalogSizeText = catalogApp.Size
                    };
```

- [ ] **Step 7: Собрать и убедиться в отсутствии ошибок**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: `Сборка успешно завершена. Предупреждений: 0 Ошибок: 0`

- [ ] **Step 8: Commit**

```bash
git add Ven4Tools/Models/App.cs Ven4Tools/ViewModels/AppRowViewModel.cs Ven4Tools/ViewModels/CatalogViewModel.cs tests/Ven4Tools.Tests/CatalogAppFieldsTests.cs
git commit -m "Прокинуть description/version/size каталога до строки (для карточки приложения)"
```

---

### Task 2: Вынести деинсталляцию в общий AppUninstallService (DRY)

`InstalledTab.xaml.cs` уже содержит рабочую логику удаления (winget → сканирование реестра `UninstallString` → тихий запуск деинсталлятора). Карточке приложения нужна та же логика — вместо копирования выносим её в сервис, которым будут пользоваться оба места.

**Files:**
- Create: `Ven4Tools/Services/AppUninstallService.cs`
- Modify: `Ven4Tools/Views/Tabs/InstalledTab.xaml.cs`

**Interfaces:**
- Produces: `AppUninstallService.TryUninstallAsync(string? wingetId, string displayName) -> Task<bool>` — используется в Task 3 (`AppCardViewModel`).

- [ ] **Step 1: Создать AppUninstallService.cs с перенесённой логикой**

Создать `Ven4Tools/Services/AppUninstallService.cs`:

```csharp
using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    // Деинсталляция: winget uninstall по ID → сканирование реестра UninstallString
    // по DisplayName → тихий запуск (msiexec /x /quiet или NSIS/Inno /S /SILENT).
    // Перенесено из InstalledTab.xaml.cs (2026-07-17), чтобы карточка приложения
    // в каталоге могла переиспользовать ту же логику вместо копирования.
    public static class AppUninstallService
    {
        public static async Task<bool> TryUninstallAsync(string? wingetId, string displayName)
        {
            // Попытка 1: winget uninstall по ID (работает для пакетов с непустым Source)
            if (!string.IsNullOrWhiteSpace(wingetId) && !wingetId.Contains('…'))
            {
                string args = $"uninstall --id \"{wingetId}\" --silent --accept-source-agreements";
                var (exitCode, _) = await WingetRunner.RunAsync(args);
                // 0 = успех, 0x8A150014 = пакет не установлен (нечего удалять — считаем успехом).
                if (exitCode == 0 || exitCode == unchecked((int)0x8A150014))
                    return true;
            }

            // Попытка 2: найти строку UninstallString в реестре по DisplayName.
            string? uninstallString = await Task.Run(() => FindUninstallString(displayName));
            if (uninstallString != null)
                return await RunUninstallStringAsync(uninstallString);

            return false;
        }

        private static string? FindUninstallString(string displayName)
        {
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            var hives = new[] { Registry.LocalMachine };

            foreach (var hive in hives)
            foreach (var keyPath in keys)
            {
                using var root = hive.OpenSubKey(keyPath);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(sub);
                    if (entry == null) continue;
                    var name = entry.GetValue("DisplayName")?.ToString();
                    if (name != null && name.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                        return entry.GetValue("UninstallString")?.ToString();
                }
            }
            return null;
        }

        private static async Task<bool> RunUninstallStringAsync(string uninstallString)
        {
            string cmd = uninstallString.Trim();
            Process? p;
            if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
            {
                var productCode = Regex.Match(cmd, @"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");
                if (!productCode.Success)
                    return false;

                var startInfo = new ProcessStartInfo(TrustedExecutablePaths.MsiExec)
                {
                    UseShellExecute = true,
                    Verb            = "runas",
                    CreateNoWindow  = true
                };
                startInfo.ArgumentList.Add("/x");
                startInfo.ArgumentList.Add(productCode.Value);
                startInfo.ArgumentList.Add("/quiet");
                startInfo.ArgumentList.Add("/norestart");
                p = Process.Start(startInfo);
            }
            else
            {
                string exe = cmd, args = "";
                if (cmd.StartsWith("\""))
                {
                    int end = cmd.IndexOf('"', 1);
                    if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); }
                }
                else
                {
                    int searchFrom = 0;
                    int bestSplit = -1;
                    while (true)
                    {
                        int sp = cmd.IndexOf(' ', searchFrom);
                        if (sp < 0) break;
                        string candidate = cmd.Substring(0, sp);
                        if (File.Exists(candidate)) bestSplit = sp;
                        searchFrom = sp + 1;
                    }

                    if (bestSplit > 0)
                    {
                        exe = cmd.Substring(0, bestSplit);
                        args = cmd.Substring(bestSplit + 1).Trim();
                    }
                    else if (!File.Exists(cmd))
                    {
                        int sp = cmd.IndexOf(' ');
                        if (sp > 0) { exe = cmd.Substring(0, sp); args = cmd.Substring(sp + 1).Trim(); }
                    }
                }
                if (!args.Contains("/S") && !args.Contains("/SILENT") && !args.Contains("/silent"))
                    args = "/S " + args;

                if (!File.Exists(exe)) return false;

                p = Process.Start(new ProcessStartInfo(exe, args)
                    { UseShellExecute = true, Verb = "runas" });
            }
            if (p == null) return false;
            using (p)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — удаление прошло успешно
                return p.ExitCode == 0 || p.ExitCode == 3010;
            }
        }
    }
}
```

- [ ] **Step 2: Собрать (новый файл должен компилироваться независимо от InstalledTab)**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: 0 ошибок (`AppUninstallService` компилируется, `InstalledTab.xaml.cs` пока не тронут — старые приватные методы там ещё есть, дублирование временное)

- [ ] **Step 3: Переключить InstalledTab.xaml.cs на новый сервис и удалить дубликат**

В `Ven4Tools/Views/Tabs/InstalledTab.xaml.cs` заменить:

```csharp
                bool ok = await TryUninstallAsync(app);
```

на:

```csharp
                bool ok = await AppUninstallService.TryUninstallAsync(app.WingetId, app.Name);
```

Затем удалить полностью три метода, которые теперь не используются: `TryUninstallAsync(InstalledApp app)`, `FindUninstallString(string displayName)`, `RunUninstallStringAsync(string uninstallString)` (весь блок от `private static async Task<bool> TryUninstallAsync(InstalledApp app)` до закрывающей `}` метода `RunUninstallStringAsync`, включая комментарии над ними).

Убрать из начала файла три строки `using`, которые использовались только в удалённых методах:

```csharp
using System.Diagnostics;
```
```csharp
using System.Text.RegularExpressions;
```
```csharp
using Microsoft.Win32;
```

- [ ] **Step 4: Собрать и убедиться в отсутствии ошибок/предупреждений**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: `Сборка успешно завершена. Предупреждений: 0 Ошибок: 0`

Если сборка покажет CS0246/CS0103 на `Process`/`Registry`/`Regex` — значит один из трёх `using` ещё используется в другом месте файла; вернуть именно тот `using` обратно (не все три сразу).

- [ ] **Step 5: Ручная проверка регрессии — удаление на вкладке «Установленные» работает как раньше**

Запустить клиент (`dotnet run --project Ven4Tools/Ven4Tools.csproj -c Release`), открыть вкладку «Установленные», убедиться что кнопка удаления по-прежнему присутствует и не выдаёт ошибок компиляции/запуска (реальное удаление приложения на своей машине не выполнять — это боковой эффект, не тестовое окружение).

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools/Services/AppUninstallService.cs Ven4Tools/Views/Tabs/InstalledTab.xaml.cs
git commit -m "Вынести деинсталляцию в AppUninstallService (DRY, переиспользуется карточкой)"
```

---

### Task 3: AppCardViewModel

**Files:**
- Create: `Ven4Tools/Services/HomepageUrlHelper.cs`
- Create: `Ven4Tools/ViewModels/AppCardViewModel.cs`
- Test: `tests/Ven4Tools.Tests/HomepageUrlHelperTests.cs`

**Interfaces:**
- Consumes: `AppRowViewModel` (Task 1 fields: `Description`, `CatalogVersion`, `CatalogSizeText`; existing `App`, `IsInstalled`, `InstalledVersion`, `CanLaunch`, `LaunchCommand`, `LaunchPath`, `PinnedVersion`, `DisplayName`, `CategoryString`, `Icon`), `AppUninstallService.TryUninstallAsync` (Task 2), `InstallationService.InstallAppAsync`/`InstallSemaphore` (существующие).
- Produces: `AppCardViewModel` с публичными `DisplayName`, `CategoryString`, `Description`, `Icon`, `OfficialSiteUrl`, `VersionText`, `SizeText`, `PackageIdText`, `IsInstalled`, `CanLaunch`, `IsBusy`, `StatusText`, командами `LaunchCommand`/`InstallCommand`/`ReinstallCommand`/`UninstallCommand`, событием `RequestClose` — используется в Task 4 (`AppCardWindow`).

- [ ] **Step 1: Написать падающий тест для HomepageUrlHelper**

Создать `tests/Ven4Tools.Tests/HomepageUrlHelperTests.cs`:

```csharp
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class HomepageUrlHelperTests
{
    [Theory]
    [InlineData("https://download.mozilla.org/?product=firefox-latest&os=win64", "https://download.mozilla.org")]
    [InlineData("https://dl.google.com/chrome/install/latest/chrome_installer.exe", "https://dl.google.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    public void ExtractHomepage_ReturnsSchemeAndHost(string? downloadUrl, string? expected)
    {
        Assert.Equal(expected, HomepageUrlHelper.ExtractHomepage(downloadUrl));
    }
}
```

- [ ] **Step 2: Запустить тест, убедиться что падает (не компилируется — класса ещё нет)**

Run: `dotnet test tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj -c Release --filter HomepageUrlHelperTests`
Expected: FAIL (ошибка компиляции — `Ven4Tools.Services.HomepageUrlHelper` не существует)

- [ ] **Step 3: Реализовать HomepageUrlHelper**

Создать `Ven4Tools/Services/HomepageUrlHelper.cs`:

```csharp
using System;

namespace Ven4Tools.Services
{
    // Официального сайта в каталоге отдельным полем нет — извлекаем домен из
    // downloadUrl как разумное приближение (см. спеку прототипа карточки).
    public static class HomepageUrlHelper
    {
        public static string? ExtractHomepage(string? downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return null;
            return $"{uri.Scheme}://{uri.Host}";
        }
    }
}
```

- [ ] **Step 4: Запустить тест, убедиться что проходит**

Run: `dotnet test tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj -c Release --filter HomepageUrlHelperTests`
Expected: PASS

- [ ] **Step 5: Реализовать AppCardViewModel**

Создать `Ven4Tools/ViewModels/AppCardViewModel.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Обёртка над AppRowViewModel для карточки приложения (прототип, см.
    // docs/superpowers/specs/2026-07-17-app-cards-prototype-design.md). Не
    // дублирует состояние строки каталога — читает его напрямую и вызывает
    // существующие/новые сервисы установки.
    public sealed class AppCardViewModel : INotifyPropertyChanged
    {
        public AppRowViewModel Row { get; }
        private readonly Func<string, Task<bool>> _confirmPmInstall;

        public event Action? RequestClose;

        public AppCardViewModel(AppRowViewModel row, Func<string, Task<bool>> confirmPmInstall)
        {
            Row = row;
            _confirmPmInstall = confirmPmInstall;

            LaunchCommand = new RelayCommand(_ =>
            {
                Row.LaunchCommand.Execute(null);
                RequestClose?.Invoke();
            }, _ => Row.CanLaunch);

            InstallCommand   = new RelayCommand(async _ => await InstallAsync(),   _ => !IsInstalled && !IsBusy);
            ReinstallCommand = new RelayCommand(async _ => await ReinstallAsync(), _ => IsInstalled && !IsBusy);
            UninstallCommand = new RelayCommand(async _ => await UninstallAsync(), _ => IsInstalled && !IsBusy);
        }

        public string DisplayName => Row.DisplayName;
        public string CategoryString => Row.CategoryString;
        public BitmapImage? Icon => Row.Icon;

        public string Description => string.IsNullOrWhiteSpace(Row.Description)
            ? "Описание отсутствует."
            : Row.Description!;

        public string? OfficialSiteUrl => HomepageUrlHelper.ExtractHomepage(Row.App.InstallerUrls.FirstOrDefault());

        public string VersionText => IsInstalled && !string.IsNullOrEmpty(Row.InstalledVersion)
            ? Row.InstalledVersion!
            : (Row.CatalogVersion is { Length: > 0 } v ? v : "—");

        public string SizeText => Row.CatalogSizeText is { Length: > 0 } s ? s : "—";

        public string PackageIdText => Row.App.AlternativeId is { Length: > 0 } wid
            ? wid
            : (Row.App.ChocoId is { Length: > 0 } cid ? cid : "—");

        public bool IsInstalled => Row.IsInstalled;
        public bool CanLaunch => Row.CanLaunch;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); RefreshCommands(); }
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        public RelayCommand LaunchCommand { get; }
        public RelayCommand InstallCommand { get; }
        public RelayCommand ReinstallCommand { get; }
        public RelayCommand UninstallCommand { get; }

        private void RefreshCommands()
        {
            InstallCommand.RaiseCanExecuteChanged();
            ReinstallCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }

        private void RaiseInstallStateChanged()
        {
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(VersionText));
            RefreshCommands();
        }

        private async Task InstallAsync()
        {
            IsBusy = true;
            StatusText = "Установка...";
            using var installService = new InstallationService();
            var progress = new Progress<AppInstallProgress>(p => StatusText = p.Status);
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                var result = await installService.InstallAppAsync(
                    Row.App, new[] { "winget", "msstore" }, CancellationToken.None, progress,
                    "C:\\", Row.PinnedVersion, _confirmPmInstall);
                if (result.Success)
                {
                    Row.IsInstalled = true;
                    StatusText = "✅ Установлено";
                }
                else
                {
                    StatusText = $"❌ {result.Message}";
                }
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsBusy = false;
                RaiseInstallStateChanged();
            }
        }

        private async Task UninstallAsync()
        {
            IsBusy = true;
            StatusText = "Удаление...";
            try
            {
                bool ok = await AppUninstallService.TryUninstallAsync(Row.App.AlternativeId, Row.DisplayName);
                if (ok)
                {
                    Row.IsInstalled = false;
                    Row.LaunchPath = null;
                    StatusText = "✅ Удалено";
                }
                else
                {
                    StatusText = "⚠ Деинсталлятор не найден";
                }
            }
            finally
            {
                IsBusy = false;
                RaiseInstallStateChanged();
            }
        }

        private async Task ReinstallAsync()
        {
            await UninstallAsync();
            if (!IsInstalled) await InstallAsync();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [ ] **Step 6: Собрать**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: `Сборка успешно завершена. Предупреждений: 0 Ошибок: 0`

- [ ] **Step 7: Прогнать все тесты проекта**

Run: `dotnet test tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj -c Release --nologo`
Expected: все тесты, включая новые `CatalogAppFieldsTests`/`HomepageUrlHelperTests`, зелёные

- [ ] **Step 8: Commit**

```bash
git add Ven4Tools/Services/HomepageUrlHelper.cs Ven4Tools/ViewModels/AppCardViewModel.cs tests/Ven4Tools.Tests/HomepageUrlHelperTests.cs
git commit -m "AppCardViewModel: обёртка над AppRowViewModel для карточки приложения"
```

---

### Task 4: AppCardWindow (модальное окно, макет B)

**Files:**
- Create: `Ven4Tools/ViewModels/InverseBooleanToVisibilityConverter.cs`
- Create: `Ven4Tools/Views/AppCardWindow.xaml`
- Create: `Ven4Tools/Views/AppCardWindow.xaml.cs`

**Interfaces:**
- Consumes: `AppCardViewModel` (Task 3) — конструктор `AppCardWindow(AppCardViewModel viewModel)`.

`BoolToVis` в существующем коде (`CatalogTab.xaml:7`) — это встроенный `BooleanToVisibilityConverter`, объявленный ЛОКАЛЬНО в ресурсах `CatalogTab.xaml`, а не в `App.xaml` — новому окну он не виден, нужно объявить свои ресурсы. Обратного (inverse) конвертера в проекте нет — создаём новый, тем же стилем, что `StringToVisibilityConverter`.

- [ ] **Step 1: Создать InverseBooleanToVisibilityConverter**

Создать `Ven4Tools/ViewModels/InverseBooleanToVisibilityConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ven4Tools.ViewModels
{
    // Для кнопки "Установить" на карточке приложения — видна, когда
    // IsInstalled == false (т.е. обратно основному BooleanToVisibilityConverter).
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && !b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Создать AppCardWindow.xaml**

```xml
<Window x:Class="Ven4Tools.Views.AppCardWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:Ven4Tools.ViewModels"
        Title="{Binding DisplayName}" Height="260" Width="620"
        WindowStartupLocation="CenterOwner"
        WindowStyle="ToolWindow" ResizeMode="NoResize"
        Background="{DynamicResource WindowBackground}">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
        <vm:InverseBooleanToVisibilityConverter x:Key="InverseBoolToVis"/>
    </Window.Resources>
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="120"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="170"/>
        </Grid.ColumnDefinitions>

        <!-- Иконка слева (растянутая — заглушка баннера, см. спеку) -->
        <Border Grid.Column="0" Background="{DynamicResource CardBackground}">
            <Image Source="{Binding Icon}" Width="72" Height="72"
                   Stretch="Uniform" HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>

        <!-- Основной контент -->
        <StackPanel Grid.Column="1" Margin="16,14,14,14">
            <TextBlock Text="{Binding DisplayName}" FontSize="18" FontWeight="Bold"
                       Foreground="{DynamicResource TextPrimary}"/>
            <TextBlock Foreground="{DynamicResource TextSecondary}" FontSize="11" Margin="0,2,0,10">
                <Run Text="{Binding CategoryString, Mode=OneWay}"/>
                <Run Text=" · "/>
                <Run Text="{Binding VersionText, Mode=OneWay}"/>
            </TextBlock>
            <TextBlock Text="{Binding Description}" TextWrapping="Wrap"
                       Foreground="{DynamicResource TextPrimary}" FontSize="12" Margin="0,0,0,10"/>
            <TextBlock Margin="0,0,0,6">
                <Hyperlink NavigateUri="{Binding OfficialSiteUrl}" RequestNavigate="Hyperlink_RequestNavigate">
                    <Run Text="🔗 "/>
                    <Run Text="{Binding OfficialSiteUrl, Mode=OneWay}"/>
                </Hyperlink>
            </TextBlock>
            <TextBlock Foreground="{DynamicResource TextSecondary}" FontSize="10" Margin="0,4,0,0">
                <Run Text="ID: "/>
                <Run Text="{Binding PackageIdText, Mode=OneWay}"/>
                <Run Text="  ·  Размер: "/>
                <Run Text="{Binding SizeText, Mode=OneWay}"/>
            </TextBlock>
            <TextBlock Text="{Binding StatusText}" Foreground="{DynamicResource TextSecondary}"
                       FontSize="11" Margin="0,10,0,0"/>
        </StackPanel>

        <!-- Панель действий справа -->
        <StackPanel Grid.Column="2" Background="{DynamicResource CardBackground}"
                    Margin="10" VerticalAlignment="Center">
            <Button Content="▶ Запустить" Height="34" Margin="0,0,0,6"
                    Command="{Binding LaunchCommand}"
                    Visibility="{Binding IsInstalled, Converter={StaticResource BoolToVis}}"
                    Background="{StaticResource BrandGreen}" Foreground="#06130D" FontWeight="Bold"/>
            <Button Content="Установить" Height="34" Margin="0,0,0,6"
                    Command="{Binding InstallCommand}"
                    Visibility="{Binding IsInstalled, Converter={StaticResource InverseBoolToVis}}"
                    Background="{StaticResource BrandGreen}" Foreground="#06130D" FontWeight="Bold"/>
            <Button Content="🔄 Переустановить" Height="30" Margin="0,0,0,6"
                    Command="{Binding ReinstallCommand}"
                    Visibility="{Binding IsInstalled, Converter={StaticResource BoolToVis}}"/>
            <Button Content="🗑 Удалить" Height="30"
                    Command="{Binding UninstallCommand}"
                    Visibility="{Binding IsInstalled, Converter={StaticResource BoolToVis}}"
                    Background="#5a1414" Foreground="#ffb4b4"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: Создать AppCardWindow.xaml.cs**

```csharp
using System.Windows;
using System.Windows.Navigation;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views
{
    public partial class AppCardWindow : Window
    {
        public AppCardWindow(AppCardViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += Close;
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
```

- [ ] **Step 4: Собрать**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/ViewModels/InverseBooleanToVisibilityConverter.cs Ven4Tools/Views/AppCardWindow.xaml Ven4Tools/Views/AppCardWindow.xaml.cs
git commit -m "AppCardWindow: модальное окно карточки приложения (макет B)"
```

---

### Task 5: Открытие карточки из каталога

**Files:**
- Modify: `Ven4Tools/ViewModels/CatalogViewModel.cs`
- Modify: `Ven4Tools/Views/Tabs/CatalogTab.xaml`

**Interfaces:**
- Consumes: `AppCardViewModel`/`AppCardWindow` (Task 3, 4).
- Produces: `CatalogViewModel.OpenCardCommand` (RelayCommand, параметр — `AppRowViewModel`).

- [ ] **Step 1: Добавить команду OpenCardCommand**

В `Ven4Tools/ViewModels/CatalogViewModel.cs`, рядом с объявлением (после строки `public RelayCommand SuggestAlternativeCommand { get; }`, строка 51):

```csharp
        public RelayCommand OpenCardCommand { get; }
```

В конструкторе, рядом с wiring `SuggestAlternativeCommand` (после блока, который заканчивается на `SuggestAlternativeCommand = new RelayCommand(async p => { if (p is AppRowViewModel row) await SuggestAlternativeAsync(row); });`):

```csharp
            OpenCardCommand = new RelayCommand(p =>
            {
                if (p is AppRowViewModel row) OpenCard(row);
            });
```

Добавить метод (рядом с `SuggestAlternativeAsync`):

```csharp
        private void OpenCard(AppRowViewModel row)
        {
            var owner = OwnerWindowProvider?.Invoke();

            Task<bool> ConfirmPmInstall(string pmName) =>
                Task.FromResult(MessageBox.Show(
                    $"Для установки приложения требуется {pmName}, который сейчас не установлен.\n\n" +
                    $"Разрешить автоматическую установку {pmName}?", $"Установка {pmName}",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

            var cardVm = new AppCardViewModel(row, ConfirmPmInstall);
            var window = new Views.AppCardWindow(cardVm) { Owner = owner };
            window.ShowDialog();
        }
```

- [ ] **Step 2: Собрать**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 3: Вынести название приложения из чекбокса и привязать клик к OpenCardCommand**

В `Ven4Tools/Views/Tabs/CatalogTab.xaml`, найти (внутри `DataTemplate` строки каталога):

```xml
                                        <CheckBox IsChecked="{Binding IsSelected}" IsEnabled="{Binding IsSelectable}"
                                                  AutomationProperties.AutomationId="{Binding CheckBoxAutomationId}"
                                                  VerticalAlignment="Center">
                                            <TextBlock Text="{Binding DisplayName}" Foreground="{Binding RowBrush}"
                                                       ToolTip="{Binding StatusTooltip}"/>
                                        </CheckBox>
```

заменить на:

```xml
                                        <CheckBox IsChecked="{Binding IsSelected}" IsEnabled="{Binding IsSelectable}"
                                                  AutomationProperties.AutomationId="{Binding CheckBoxAutomationId}"
                                                  VerticalAlignment="Center"/>

                                        <TextBlock Text="{Binding DisplayName}" Foreground="{Binding RowBrush}"
                                                   ToolTip="{Binding StatusTooltip}" VerticalAlignment="Center"
                                                   Margin="4,0,0,0" Cursor="Hand">
                                            <TextBlock.InputBindings>
                                                <MouseBinding MouseAction="LeftClick"
                                                              Command="{Binding DataContext.OpenCardCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                              CommandParameter="{Binding}"/>
                                            </TextBlock.InputBindings>
                                        </TextBlock>
```

(Клик по самому чекбоксу по-прежнему переключает выбор — это уже штатное поведение `CheckBox`, ничего дополнительно для этого делать не нужно.)

- [ ] **Step 4: Собрать**

Run: `dotnet build Ven4Tools/Ven4Tools.csproj -c Release --nologo`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 5: Прогнать все тесты**

Run: `dotnet test tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj -c Release --nologo`
Expected: все тесты зелёные

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools/ViewModels/CatalogViewModel.cs Ven4Tools/Views/Tabs/CatalogTab.xaml
git commit -m "Каталог: клик по названию приложения открывает карточку"
```

---

### Task 6: Ручная проверка живьём (скриншоты)

**Files:** нет изменений — только проверка.

- [ ] **Step 1: Запустить клиент**

Run: `dotnet run --project Ven4Tools/Ven4Tools.csproj -c Release`

(Клиент elevated — потребуется подтверждение UAC, если сборка запускается из неэлевированной сессии.)

- [ ] **Step 2: Открыть карточку установленного приложения**

В каталоге кликнуть по названию любого установленного приложения (например Яндекс.Браузер, если установлен на машине). Проверить: банер/иконка отображается, описание не пустое, ссылка на сайт кликабельна, видны кнопки «▶ Запустить»/«🔄 Переустановить»/«🗑 Удалить», версия/размер/ID заполнены не «—» там, где данные есть в каталоге.

- [ ] **Step 3: Открыть карточку неустановленного приложения**

Кликнуть по названию неустановленного приложения. Проверить: вместо «Запустить» показана «Установить», «Переустановить»/«Удалить» не видны.

- [ ] **Step 4: Проверить закрытие карточки при запуске**

На карточке установленного приложения нажать «▶ Запустить» — окно карточки должно закрыться сразу после вызова команды запуска (приложение стартует отдельным процессом).

- [ ] **Step 5: Сделать скриншоты обоих состояний карточки для отчёта пользователю**

Скриншот окна `AppCardWindow` в состоянии «установлено» и в состоянии «не установлено» (тем же способом, каким делались скриншоты каталога при проверке фикса winget в этой же сессии — захват окна процесса через `GetWindowRect`+`CopyFromScreen`).
