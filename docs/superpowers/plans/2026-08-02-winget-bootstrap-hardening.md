# Усиление bootstrap winget в лаунчере — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать самостоятельную установку winget лаунчером (сайдлоад MSIX при первом запуске) максимально близкой к гарантированной и честной по диагностике — расшифровка HRESULT-ошибок `Add-AppxPackage`, автофикс самого частого конфликта (VCLibs), поддержка ARM64, резервная загрузка компонентов с собственного CDN, и точное различение «не получилось технически, можно повторить» от «эта редакция/сборка Windows не поддерживает сайдлоад в принципе».

**Architecture:** Не меняет сам механизм (сайдлоад MSIX через `Add-AppxPackage`, см. `MainWindow.Components.Winget.cs`) — добавляет: (1) `WingetInstallErrorMapper` для разбора HRESULT из stderr PowerShell, (2) единственный автоматический retry с удалением конфликтующей VCLibs, (3) выбор ARM64/x64 URL по `RuntimeInformation.OSArchitecture`, (4) `FallbackDownloader`-цепочку CDN→Microsoft для всех 4 файлов (сейчас только Microsoft, без резерва), (5) `WindowsEditionInspector` для предварительной честной диагностики LTSC/отсутствия AppX-стека до попытки установки.

**Tech Stack:** .NET 8, WPF (лаунчер), `System.Diagnostics.Process`+PowerShell (существующий механизм), `System.Runtime.InteropServices.RuntimeInformation`, `Microsoft.Win32.Registry`, xUnit.

## Global Constraints

- Обсуждение и обоснование каждого пункта — см. диалог сессии 2026-08-02 (расшифровка ошибок winget/choco, вшивание vs CDN, детект архитектуры, LTSC/AppX-стоп-события). Отдельного файла-спеки для этой части не заводилось — архитектура зафиксирована прямо здесь.
- НЕ вшивать сам установщик winget/зависимости в лаунчер (решение принято в обсуждении — см. Global Constraints выше: устаревает быстрее цикла релизов лаунчера, раздувает инсталлятор, подпись всё равно проверяется на лету).
- Никаких изменений в логике установки ПРИЛОЖЕНИЙ из каталога через winget/choco (`Ven4Tools/Services/WingetErrorMapper.cs`, `ChocoErrorMapper.cs`, `InstallationService.*.cs`) — это отдельная, уже существующая и не тронутая система на стороне КЛИЕНТА. Этот план — только про то, как лаунчер устанавливает САМ winget при первом запуске (`Ven4Tools.Launcher/MainWindow.Components.Winget.cs`).
- 0 ошибок, 0 предупреждений при `dotnet build` перед каждым коммитом.
- Тексты — только на русском. Не упоминать вспомогательные инструменты разработки нигде в репозитории.

---

## File Structure

| Файл | Назначение |
|---|---|
| `Ven4Tools.Launcher/Services/WingetInstallErrorMapper.cs` | Новый — разбор HRESULT из stderr `Add-AppxPackage`, известные коды |
| `Ven4Tools.Launcher/MainWindow.Components.Winget.cs` | **Изменить** — использовать маппер, исправить всегда-`true` возврат, VCLibs-автофикс, ARM64-ветки URL, CDN-цепочка |
| `Ven4Tools.Launcher/Services/WindowsEditionInspector.cs` | Новый — детект LTSC / доступности AppX-стека |
| `Ven4Tools.Launcher/MainWindow.Components.cs` | **Изменить** — использовать `WindowsEditionInspector` в `CheckComponentsAutoAsync`/`CheckComponentsInteractiveAsync` |
| `Ven4Tools.Launcher/Services/DownloadSource.cs` | Не меняется — `cdn.ven4tools.ru` уже в allowlist `DownloadValidator` без ограничения по пути |
| `Tools/mirror-winget-components.ps1` | Новый — синк 4 файлов Microsoft на `cdn.ven4tools.ru/winget-components/`, разовый + под cron на VPS |
| `tests/Ven4Tools.Tests/WingetInstallErrorMapperTests.cs` | Новый |
| `tests/Ven4Tools.Tests/WindowsEditionInspectorTests.cs` | Новый (только чистые/детерминированные части — реальное чтение реестра/PowerShell не мокается, см. Task 5) |

---

### Task 1: `WingetInstallErrorMapper` + исправление скрытого бага возврата

**Files:**
- Create: `Ven4Tools.Launcher/Services/WingetInstallErrorMapper.cs`
- Modify: `Ven4Tools.Launcher/MainWindow.Components.Winget.cs`
- Test: `tests/Ven4Tools.Tests/WingetInstallErrorMapperTests.cs`

**Interfaces:**
- Produces: `internal static class WingetInstallErrorMapper { public static string MapPowerShellStderr(string? stderr); public static bool IsVCLibsConflict(string? stderr); }`.

**Найденный попутно баг (не гипотеза — прочитан в текущем коде):** `RunWingetInstallScriptAsync` в `MainWindow.Components.Winget.cs` (текущие строки ~368–377) всегда `return true;` в конце метода, независимо от `proc.ExitCode` — при провале `Add-AppxPackage` метод не сообщает вызывающему коду ничего, кроме строки в логе. `InstallWingetAsync` полагается на последующую независимую перепроверку `CheckWingetWithVersionAsync()`, поэтому конечный результат («winget не найден») всё равно верный — но конкретная причина неудачи никогда не долетает до diagnostics/UI отдельно от сырого stderr. Этот план это исправляет попутно, т.к. без исправленного возврата некуда подключить маппер и автофикс (Task 2).

- [ ] **Step 1: Написать падающий тест маппера**

```csharp
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class WingetInstallErrorMapperTests
{
    [Fact]
    public void VCLibsConflict_IsMappedWithExplanation()
    {
        string stderr = "Add-AppxPackage : Deployment failed with HRESULT: 0x80073CF3, " +
            "The package could not satisfy a dependency on Microsoft.VCLibs.140.00.UWPDesktop.";
        string message = WingetInstallErrorMapper.MapPowerShellStderr(stderr);
        Assert.Contains("0x80073CF3", message);
        Assert.Contains("VCLibs", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResourceInUse_IsMapped()
    {
        string stderr = "Deployment failed with HRESULT: 0x80073D02, resources it modifies are currently in use.";
        string message = WingetInstallErrorMapper.MapPowerShellStderr(stderr);
        Assert.Contains("0x80073D02", message);
        Assert.Contains("заняты", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownHResult_StillReportsCode()
    {
        string message = WingetInstallErrorMapper.MapPowerShellStderr("HRESULT: 0xDEADBEEF, something else");
        Assert.Contains("0xDEADBEEF", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("неизвестный", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no hresult here at all")]
    public void MissingOrNoHResult_FallsBackHonestly(string? stderr)
    {
        string message = WingetInstallErrorMapper.MapPowerShellStderr(stderr);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void IsVCLibsConflict_DetectsKnownCode()
    {
        Assert.True(WingetInstallErrorMapper.IsVCLibsConflict("HRESULT: 0x80073CF3, ..."));
        Assert.False(WingetInstallErrorMapper.IsVCLibsConflict("HRESULT: 0x80073D02, ..."));
        Assert.False(WingetInstallErrorMapper.IsVCLibsConflict(null));
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~WingetInstallErrorMapperTests"`
Expected: FAIL — класс не существует.

- [ ] **Step 3: Реализовать `WingetInstallErrorMapper`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Расшифровка HRESULT-ошибок Add-AppxPackage (сайдлоад winget при первом
/// запуске лаунчера) — в отличие от WingetErrorMapper/ChocoErrorMapper на
/// стороне клиента (расшифровывающих exit-код winget.exe/choco.exe при
/// установке ПРИЛОЖЕНИЙ), здесь источник ошибок — COM-подсистема AppX,
/// падающая HRESULT-кодами в тексте stderr PowerShell, а не простым exit-кодом.
/// </summary>
internal static class WingetInstallErrorMapper
{
    private static readonly Dictionary<uint, string> KnownHResults = new()
    {
        { 0x80073CF3, "конфликт версий зависимости (обычно Microsoft.VCLibs.140.00.UWPDesktop) — на системе уже установлена другая версия." },
        { 0x80073D02, "файлы пакета заняты другим процессом — закройте связанные программы и повторите." },
        { 0x80073CFF, "сбой развёртывания AppX — возможно, повреждён кэш компонентов Windows." },
        { 0x80073CFD, "сбой развёртывания AppX — возможно, повреждён кэш компонентов Windows." },
    };

    private static readonly Regex HResultPattern = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

    public static string MapPowerShellStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "winget не установился, PowerShell не сообщил подробностей.";

        Match match = HResultPattern.Match(stderr);
        if (!match.Success ||
            !uint.TryParse(match.Value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code))
            return "winget не установился — код HRESULT не распознан в выводе PowerShell, подробности в логе.";

        string suffix = KnownHResults.TryGetValue(code, out var known)
            ? known
            : "неизвестный код развёртывания AppX, подробности — в логе.";
        return $"{match.Value}: {suffix}";
    }

    public static bool IsVCLibsConflict(string? stderr) =>
        stderr != null && stderr.Contains("0x80073CF3", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Запустить тесты, убедиться, что проходят**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~WingetInstallErrorMapperTests"`
Expected: PASS (7/7).

- [ ] **Step 5: Подключить маппер и исправить возврат `RunWingetInstallScriptAsync`**

В `Ven4Tools.Launcher/MainWindow.Components.Winget.cs` изменить сигнатуру и тело `RunWingetInstallScriptAsync` (сейчас `Task<bool>`, всегда `return true`):

```csharp
        private async Task<(bool Ok, string? ErrorDetail)> RunWingetInstallScriptAsync(
            string tempVcLibs, string tempUiXaml, string tempMsix, CancellationToken ct)
        {
            using var vcLibsHandle = new FileStream(tempVcLibs, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var uiXamlHandle = new FileStream(tempUiXaml, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var msixHandle   = new FileStream(tempMsix,   FileMode.Open, FileAccess.Read, FileShare.Read);

            foreach (var (path, label) in new[] { (tempVcLibs, "VCLibs"), (tempUiXaml, "UI.Xaml"), (tempMsix, "winget") })
            {
                if (!AuthenticodeVerifier.IsSignedByMicrosoft(path, out string sigError))
                {
                    AddLog($"⛔ Подлинность пакета {label} не подтверждена ({sigError}) — установка отменена");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Подлинность не подтверждена");
                    return (false, $"подлинность пакета {label} не подтверждена");
                }
            }
            AddLog("✅ Подпись Microsoft подтверждена для всех пакетов");

            AddLog("📦 Установка winget...");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Установка...");

            string tempScript = Path.Combine(Path.GetTempPath(), $"winget_install_{Guid.NewGuid():N}.ps1");
            try
            {
                File.WriteAllText(tempScript,
                    $"$ErrorActionPreference = 'Stop'\r\n" +
                    $"try {{ Add-AppxPackage -Path '{tempVcLibs.Replace("'", "''")}' }} catch {{}}\r\n" +
                    $"try {{ Add-AppxPackage -Path '{tempUiXaml.Replace("'", "''")}' }} catch {{}}\r\n" +
                    $"Add-AppxPackage -Path '{tempMsix.Replace("'", "''")}' -ForceApplicationShutdown\r\n",
                    Encoding.UTF8);

                using var scriptGuard = new FileStream(tempScript, FileMode.Open, FileAccess.Read, FileShare.Read);

                var psi = new ProcessStartInfo
                {
                    FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                    Arguments              = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (false, "PowerShell не запустился");

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                string stderr  = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                await stdoutTask;

                if (proc.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
                {
                    string detail = WingetInstallErrorMapper.MapPowerShellStderr(stderr);
                    AddLog($"⚠️ PowerShell: {detail}");
                    return (proc.ExitCode == 0, detail);
                }
                return (true, null);
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
            }
        }
```

Обновить вызывающий код в `InstallWingetAsync` (текущая строка `if (!await RunWingetInstallScriptAsync(...)) return;`):

```csharp
                var scriptResult = await RunWingetInstallScriptAsync(tempVcLibs, tempUiXaml, tempMsix, ct);
                if (!scriptResult.Ok)
                {
                    AddLog($"❌ Установка winget не удалась: {scriptResult.ErrorDetail}");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка установки");
                    if (interactive)
                        System.Windows.MessageBox.Show(
                            $"Не удалось установить winget: {scriptResult.ErrorDetail}",
                            "Ошибка установки winget", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
```

- [ ] **Step 6: Собрать, прогнать существующие тесты компонентов**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.
Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~Winget"` — без регрессий (существующие тесты `WingetErrorMapperTests` на стороне клиента не затронуты, это другой класс).

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools.Launcher/Services/WingetInstallErrorMapper.cs Ven4Tools.Launcher/MainWindow.Components.Winget.cs tests/Ven4Tools.Tests/WingetInstallErrorMapperTests.cs
git commit -m "Лаунчер: расшифровка HRESULT-ошибок Add-AppxPackage при установке winget"
```

---

### Task 2: Автофикс конфликта VCLibs (0x80073CF3)

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Components.Winget.cs`

**Interfaces:**
- Consumes: `WingetInstallErrorMapper.IsVCLibsConflict` (Task 1).

**Риск, требующий явного логирования:** `Remove-AppxPackage` для `Microsoft.VCLibs.140.00.UWPDesktop` может задеть другое, не связанное с Ven4Tools UWP-приложение, если оно зависит именно от той версии, которая будет удалена. Поэтому: (1) попытка только ОДНА, не в цикле; (2) явный лог с именами удаляемых пакетов ДО удаления; (3) срабатывает только при точном совпадении кода `0x80073CF3`, не при любой прочей ошибке.

- [ ] **Step 1: Добавить retry с автофиксом в `InstallWingetAsync`**

В `Ven4Tools.Launcher/MainWindow.Components.Winget.cs`, заменить одиночный вызов `RunWingetInstallScriptAsync` (из Task 1, Step 5) на вызов с одной повторной попыткой при обнаруженном конфликте VCLibs:

```csharp
                var scriptResult = await RunWingetInstallScriptAsync(tempVcLibs, tempUiXaml, tempMsix, ct);
                if (!scriptResult.Ok && WingetInstallErrorMapper.IsVCLibsConflict(scriptResult.ErrorDetail))
                {
                    AddLog("🔧 Обнаружен конфликт версий VCLibs — пробую автоматически удалить конфликтующую версию и повторить...");
                    bool cleaned = await TryRemoveConflictingVCLibsAsync(ct);
                    if (cleaned)
                    {
                        AddLog("🔁 Повторная установка после очистки VCLibs...");
                        scriptResult = await RunWingetInstallScriptAsync(tempVcLibs, tempUiXaml, tempMsix, ct);
                    }
                    else
                    {
                        AddLog("⚠️ Не удалось автоматически устранить конфликт VCLibs");
                    }
                }

                if (!scriptResult.Ok)
                {
                    AddLog($"❌ Установка winget не удалась: {scriptResult.ErrorDetail}");
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка установки");
                    if (interactive)
                        System.Windows.MessageBox.Show(
                            $"Не удалось установить winget: {scriptResult.ErrorDetail}",
                            "Ошибка установки winget", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
```

- [ ] **Step 2: Реализовать `TryRemoveConflictingVCLibsAsync`**

Добавить в тот же файл:

```csharp
        // Единственная автоматическая попытка устранить самый частый сбой установки
        // winget (0x80073CF3) — конфликт версий Microsoft.VCLibs.140.00.UWPDesktop.
        // Риск: может задеть другое UWP-приложение, зависящее именно от удаляемой
        // версии — поэтому не зацикливается и явно логирует, что именно удаляет,
        // ДО самого удаления (не постфактум).
        private async Task<bool> TryRemoveConflictingVCLibsAsync(CancellationToken ct)
        {
            try
            {
                const string listScript =
                    "(Get-AppxPackage Microsoft.VCLibs.140.00.UWPDesktop*).PackageFullName";
                var listPsi = new ProcessStartInfo
                {
                    FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                    Arguments              = $"-NoProfile -Command \"{listScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var listProc = Process.Start(listPsi);
                if (listProc == null) return false;
                string listOutput = await listProc.StandardOutput.ReadToEndAsync(ct);
                await listProc.WaitForExitAsync(ct);

                var packageNames = listOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
                if (packageNames.Length == 0)
                {
                    AddLog("⚠️ Конфликтующая версия VCLibs не найдена через Get-AppxPackage — нечего удалять");
                    return false;
                }

                AddLog($"🗑 Удаляю пакеты: {string.Join(", ", packageNames)}");

                const string removeScript =
                    "Get-AppxPackage Microsoft.VCLibs.140.00.UWPDesktop* | Remove-AppxPackage";
                var removePsi = new ProcessStartInfo
                {
                    FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                    Arguments              = $"-NoProfile -Command \"{removeScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding  = Encoding.UTF8
                };
                using var removeProc = Process.Start(removePsi);
                if (removeProc == null) return false;
                string removeStderr = await removeProc.StandardError.ReadToEndAsync(ct);
                await removeProc.WaitForExitAsync(ct);

                if (removeProc.ExitCode != 0)
                {
                    AddLog($"⚠️ Remove-AppxPackage завершился с ошибкой: {removeStderr.Trim()}");
                    return false;
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"⚠️ Не удалось выполнить автофикс VCLibs: {ex.Message}");
                return false;
            }
        }
```

- [ ] **Step 3: Собрать, проверить**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.

Ручная проверка автофикса требует реальной машины с конфликтом VCLibs — такую специально не воспроизвести детерминированно в CI, поэтому автотеста на это нет (аналогично остальному коду `MainWindow.Components.Winget.cs`, который тоже не покрыт юнит-тестами — реальные `Process.Start`/PowerShell не мокаются в этом файле нигде, см. существующий код). Ограничиться сборкой + code review на этом шаге.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools.Launcher/MainWindow.Components.Winget.cs
git commit -m "Лаунчер: автофикс конфликта версий VCLibs (0x80073CF3) при установке winget"
```

---

### Task 3: Детект архитектуры (ARM64)

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Components.MicrosoftInstallers.cs`

**Interfaces:**
- Produces: `private static bool IsArm64` (readonly, вычисляется через `RuntimeInformation.OSArchitecture`), используется при выборе всех трёх URL зависимостей.

**Проверить перед реализацией (не гипотеза, а факт, который нужно свежо сверить):** имена ARM64-ассетов должны совпадать с реально опубликованными Microsoft. x64-варианты уже в коде (`Microsoft.VCLibs.x64.14.00.Desktop.appx`, `Microsoft.UI.Xaml.2.8.x64.appx`, `windowsappruntimeinstall-x64.exe`) — ARM64 предположительно называются заменой `x64`→`arm64` в том же месте пути/имени (стандартный паттерн именования Microsoft для этих конкретных пакетов), но перед коммитом Step 2 обязательно свериться:
- VCLibs ARM64: открыть `https://aka.ms/Microsoft.VCLibs.arm64.14.00.Desktop.appx` — должен быть прямой редирект на .appx, не 404.
- UI.Xaml ARM64: открыть страницу релиза `https://github.com/microsoft/microsoft-ui-xaml/releases/tag/v2.8.6` и проверить точное имя ARM64-ассета в списке файлов (может быть `Microsoft.UI.Xaml.2.8.arm64.appx` либо иначе).
- Windows App Runtime ARM64: открыть `https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-arm64.exe` — тот же принцип, что и уже запинненный x64 (версия зафиксирована в URL, вечной ссылки «latest» нет — тот же факт, что уже задокументирован в коде для x64).

- [ ] **Step 1: Написать падающий тест выбора URL (чистая функция, без сети)**

```csharp
using Ven4Tools.Launcher;

namespace Ven4Tools.Tests;

public sealed class WingetComponentUrlSelectionTests
{
    [Theory]
    [InlineData(true, "arm64")]
    [InlineData(false, "x64")]
    public void VCLibsUrl_MatchesArchitecture(bool isArm64, string expectedArchSegment)
    {
        string url = MainWindowWingetUrls.ResolveVCLibsUrl(isArm64);
        Assert.Contains(expectedArchSegment, url, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "arm64")]
    [InlineData(false, "x64")]
    public void UiXamlUrl_MatchesArchitecture(bool isArm64, string expectedArchSegment)
    {
        string url = MainWindowWingetUrls.ResolveUiXamlUrl(isArm64);
        Assert.Contains(expectedArchSegment, url, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "arm64")]
    [InlineData(false, "x64")]
    public void WindowsAppRuntimeUrl_MatchesArchitecture(bool isArm64, string expectedArchSegment)
    {
        string url = MainWindowWingetUrls.ResolveWindowsAppRuntimeUrl(isArm64);
        Assert.Contains(expectedArchSegment, url, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Убедиться, что тесты падают**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~WingetComponentUrlSelectionTests"`
Expected: FAIL — `MainWindowWingetUrls` не существует.

- [ ] **Step 3: Вынести резолвинг URL в чистый статический класс + подключить в `MainWindow.Components.MicrosoftInstallers.cs`**

Новый файл `Ven4Tools.Launcher/MainWindowWingetUrls.cs` (чистая функция — вынесена отдельно от `MainWindow`, чтобы быть тестируемой без WPF-контекста):

```csharp
namespace Ven4Tools.Launcher;

/// <summary>
/// Резолвинг URL зависимостей winget по архитектуре — чистые функции без
/// сети, вынесены отдельно от MainWindow ради тестируемости. ПЕРЕД
/// использованием ARM64-веток свериться, что имена ARM64-ассетов реально
/// опубликованы Microsoft под этими именами (см. Task 3, шаг проверки
/// в плане реализации) — x64-имена уже подтверждены существующим кодом.
/// </summary>
internal static class MainWindowWingetUrls
{
    public static string ResolveVCLibsUrl(bool isArm64) => isArm64
        ? "https://aka.ms/Microsoft.VCLibs.arm64.14.00.Desktop.appx"
        : "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx";

    public static string ResolveUiXamlUrl(bool isArm64) => isArm64
        ? "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.arm64.appx"
        : "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx";

    // Та же версия 1.8.260710003, что уже запинена для x64 (нет вечной ссылки
    // «latest» — см. комментарий у существующей x64-константы в
    // MainWindow.Components.MicrosoftInstallers.cs).
    public static string ResolveWindowsAppRuntimeUrl(bool isArm64) => isArm64
        ? "https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-arm64.exe"
        : "https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe";
}
```

В `Ven4Tools.Launcher/MainWindow.Components.Winget.cs`, метод `DownloadWingetPackagesAsync`, заменить захардкоженные x64-URL:

```csharp
        private async Task DownloadWingetPackagesAsync(
            string msixUrl, string tempVcLibs, string tempUiXaml, string tempAppRuntime, string tempMsix, CancellationToken ct)
        {
            bool isArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                System.Runtime.InteropServices.Architecture.Arm64;
            if (isArm64) AddLog("ℹ️ Обнаружена архитектура ARM64 — используются соответствующие пакеты");

            AddLog("⬇️ Скачивание зависимостей...");
            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Скачивание зависимостей...");

            var vcLibsTask = DownloadTrustedFileAsync(
                MainWindowWingetUrls.ResolveVCLibsUrl(isArm64), tempVcLibs, "VCLibs", reportProgress: false, ct);
            var uiXamlTask = DownloadTrustedFileAsync(
                MainWindowWingetUrls.ResolveUiXamlUrl(isArm64), tempUiXaml, "UI.Xaml", reportProgress: false, ct);
            var appRuntimeTask = DownloadTrustedFileAsync(
                MainWindowWingetUrls.ResolveWindowsAppRuntimeUrl(isArm64), tempAppRuntime, "Windows App Runtime", reportProgress: false, ct);

            await Task.WhenAll(vcLibsTask, uiXamlTask, appRuntimeTask);

            AddLog($"⬇️ Скачивание winget ({msixUrl.Split('/').Last()})...");
            await DownloadTrustedFileAsync(msixUrl, tempMsix, "Winget", reportProgress: true, ct);
        }
```

Удалить старую константу `WindowsAppRuntimeInstallerUrl` (заменена методом `MainWindowWingetUrls.ResolveWindowsAppRuntimeUrl`) — её комментарий про отсутствие вечной ссылки «latest» переносится в новый файл (уже перенесён в Step 3 выше).

- [ ] **Step 4: Запустить тесты, убедиться, что проходят**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~WingetComponentUrlSelectionTests"`
Expected: PASS (6/6).

- [ ] **Step 5: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools.Launcher/MainWindowWingetUrls.cs Ven4Tools.Launcher/MainWindow.Components.Winget.cs tests/Ven4Tools.Tests/WingetComponentUrlSelectionTests.cs
git commit -m "Лаунчер: детект архитектуры ARM64 для зависимостей winget (VCLibs/UI.Xaml/WindowsAppRuntime)"
```

---

### Task 4: Резервная загрузка компонентов winget с CDN

**Files:**
- Modify: `Ven4Tools.Launcher/MainWindow.Components.cs` (метод `DownloadTrustedFileAsync`)
- Create: `Tools/mirror-winget-components.ps1`

**Interfaces:**
- Consumes: существующие `FallbackDownloader`, `DownloadCandidate`, `_httpClient` (`MainWindow.Components.cs`). `DownloadValidator.IsAllowedUri` уже разрешает `cdn.ven4tools.ru` без ограничения по пути (в отличие от `ven4tools.ru`, которому нужен префикс `/releases/`) — изменений в allowlist не требуется.

**Обоснование:** `ResolveWingetMsixUrlAsync`/`DownloadWingetPackagesAsync` сейчас бьют напрямую в `api.github.com`/`aka.ms`/`github.com` без единого резерва — единственное место в лаунчере без `FallbackDownloader`-цепочки (у скачивания клиента она есть: CDN→CDN IP→зеркало→GitHub). При блокировке GitHub/aka.ms по SNI winget не установится вообще, хотя сеть у машины технически есть.

- [ ] **Step 1: Расширить `DownloadTrustedFileAsync` до цепочки CDN→оригинал**

В `Ven4Tools.Launcher/MainWindow.Components.cs`, заменить текущую реализацию (единственный кандидат — оригинальный URL):

```csharp
        // Единая загрузка файла компонента winget (VCLibs/UI.Xaml/WindowsAppRuntime/
        // сам msixbundle) с резервом на собственный CDN: если оригинальный источник
        // Microsoft (aka.ms/github.com) заблокирован/недоступен именно лаунчеру, тот
        // же файл (зеркалируется Tools/mirror-winget-components.ps1) отдаётся с
        // cdn.ven4tools.ru/winget-components/<имя файла>. CDN — ПЕРВЫЙ кандидат
        // (обычно быстрее и без риска блокировки), оригинал — резерв, а не наоборот,
        // т.к. зеркало синкается регулярно и файл на нём тот же самый (проверяется
        // Authenticode-подписью Microsoft ниже по стеку вызовов независимо от того,
        // какой источник сработал).
        private async Task DownloadTrustedFileAsync(
            string url, string destPath, string label, bool reportProgress, CancellationToken ct)
        {
            Action<long, long?>? progress = null;
            if (reportProgress)
                progress = (received, total) =>
                {
                    if (total is > 0)
                    {
                        int pct = (int)((double)received / total.Value * 100);
                        Dispatcher.Invoke(() => { progressDownload.Value = pct; txtDownloadStatus.Text = $"{label}: {pct}%"; });
                    }
                };

            string fileName = url.Split('/').Last();
            string mirrorUrl = $"https://cdn.ven4tools.ru/winget-components/{fileName}";

            var candidates = new[]
            {
                new DownloadCandidate(mirrorUrl, _httpClient, "CDN"),
                new DownloadCandidate(url, _httpClient, "Источник")
            };

            var downloader = new FallbackDownloader();
            using var _ = await downloader.DownloadAsync(candidates, destPath, ct, expectedSha256: null, progress: progress,
                switchingTo: (nextLabel, reason) => AddLog($"⚠️ {label}: CDN-зеркало {reason}, пробую {nextLabel}..."));
        }
```

- [ ] **Step 2: Написать скрипт синка на CDN**

`Tools/mirror-winget-components.ps1`:
```powershell
<#
.SYNOPSIS
Синхронизирует 4 файла компонентов winget (VCLibs/UI.Xaml/WindowsAppRuntime x64+ARM64,
сам актуальный msixbundle winget) с оригинальных источников Microsoft на
cdn.ven4tools.ru/winget-components/ — резервный путь на случай блокировки
aka.ms/github.com именно у лаунчера (см. MainWindow.Components.cs, DownloadTrustedFileAsync).

.DESCRIPTION
Запускать вручную при обновлении версии Windows App Runtime (см. пин версии в
MainWindowWingetUrls.cs) или периодически по cron на VPS для актуального
msixbundle winget (у него нет версии в URL — /releases/latest у GitHub API).

.EXAMPLE
.\Tools\mirror-winget-components.ps1
#>
$ErrorActionPreference = "Stop"

$files = @(
    "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx",
    "https://aka.ms/Microsoft.VCLibs.arm64.14.00.Desktop.appx",
    "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx",
    "https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.arm64.appx",
    "https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe",
    "https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-arm64.exe"
)

$releaseJson = Invoke-RestMethod "https://api.github.com/repos/microsoft/winget-cli/releases/latest"
$msixAsset = $releaseJson.assets | Where-Object { $_.name -like "*DesktopAppInstaller*.msixbundle" } | Select-Object -First 1
if ($msixAsset) { $files += $msixAsset.browser_download_url }

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ven4tools-winget-mirror-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try
{
    foreach ($url in $files)
    {
        $fileName = $url.Split('/')[-1]
        $localPath = Join-Path $tempDir $fileName
        Write-Host "Скачиваю $fileName..."
        Invoke-WebRequest $url -OutFile $localPath -UseBasicParsing
        scp $localPath "jump:/tmp/$fileName"
        ssh jump "mv /tmp/$fileName /var/www/cdn/winget-components/$fileName && chown root:root /var/www/cdn/winget-components/$fileName && chmod 644 /var/www/cdn/winget-components/$fileName"
    }
    Write-Host "Готово: $($files.Count) файлов зеркалировано на cdn.ven4tools.ru/winget-components/"
}
finally
{
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
```

Перед первым запуском на VPS создать каталог: `ssh jump "mkdir -p /var/www/cdn/winget-components && chown root:root /var/www/cdn/winget-components"`.

- [ ] **Step 3: Собрать, вручную проверить, что резервный путь реально переключается**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.

Ручная проверка (нет автотеста — реальная сеть/CDN, тот же принцип, что у остального содержимого этого файла): временно испортить URL зеркала (например, добавить несуществующий путь) и убедиться, что лог показывает переключение на «Источник» и установка всё равно завершается.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools.Launcher/MainWindow.Components.cs Tools/mirror-winget-components.ps1
git commit -m "Лаунчер: резервная загрузка компонентов winget с CDN, скрипт синка зеркала"
```

Запустить `Tools/mirror-winget-components.ps1` вручную один раз до релиза этой версии лаунчера — иначе резервный путь будет постоянно проваливаться на оригинал (не критично, `FallbackDownloader` просто перейдёт ко второму кандидату, но резерва по факту не будет, пока файлы не окажутся на CDN).

---

### Task 5: `WindowsEditionInspector` — честная диагностика LTSC / отсутствия AppX-стека

**Files:**
- Create: `Ven4Tools.Launcher/Services/WindowsEditionInspector.cs`
- Modify: `Ven4Tools.Launcher/MainWindow.Components.cs` (`CheckComponentsAutoAsync`, `CheckComponentsInteractiveAsync`)
- Test: `tests/Ven4Tools.Tests/WindowsEditionInspectorTests.cs`

**Interfaces:**
- Produces:
```csharp
internal static class WindowsEditionInspector
{
    public static bool IsLikelyLtsc(); // синхронно, чтение реестра
    public static Task<bool> IsAppxStackAvailableAsync(CancellationToken token); // короткий пробный PowerShell-вызов
}
```

**Ограничение честности:** `EditionID` реестра для LTSC-редакций исторически — `"EnterpriseS"` (2015/2016 LTSB и 2019/2021 LTSC используют то же значение) — это наиболее часто цитируемое, но НЕ официально задокументированное Microsoft значение на 100% для всех будущих версий. Трактовать результат `IsLikelyLtsc()` как «похоже на LTSC», не как категоричный факт — отсюда и `Likely` в имени метода, и формулировка сообщения пользователю ниже («вероятно», не «точно»).

- [ ] **Step 1: Написать падающий тест на чистую часть (реестровое чтение через внедряемую функцию)**

```csharp
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class WindowsEditionInspectorTests
{
    [Theory]
    [InlineData("EnterpriseS", true)]
    [InlineData("Enterprise", false)]
    [InlineData("Professional", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ClassifyEditionId_DetectsLtscPattern(string? editionId, bool expectedLtsc)
    {
        Assert.Equal(expectedLtsc, WindowsEditionInspector.ClassifyEditionId(editionId));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~WindowsEditionInspectorTests"`
Expected: FAIL — класс не существует.

- [ ] **Step 3: Реализовать `WindowsEditionInspector`**

```csharp
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Честная предпроверка перед установкой winget: различает «не получилось
/// технически, можно повторить» от «эта редакция/сборка Windows не
/// поддерживает сайдлоад категорически, повтор бессмысленен». Без этого
/// CheckComponentsInteractiveAsync после провала всегда пишет «возможно,
/// требуется перезагрузка» — что прямая неправда для LTSC/урезанных сборок
/// без AppX-стека, где перезагрузка никогда не поможет.
/// </summary>
internal static class WindowsEditionInspector
{
    private const string EditionKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>
    /// EditionID "EnterpriseS" — наиболее часто встречающееся значение для LTSB/LTSC
    /// (2015 LTSB, 2016 LTSB, 2019 LTSC, 2021 LTSC используют то же имя) — не
    /// официально документированный на 100% признак, отсюда "Likely" в имени метода.
    /// </summary>
    public static bool ClassifyEditionId(string? editionId) =>
        !string.IsNullOrEmpty(editionId) &&
        editionId.Equals("EnterpriseS", StringComparison.OrdinalIgnoreCase);

    public static bool IsLikelyLtsc()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(EditionKeyPath);
            return ClassifyEditionId(key?.GetValue("EditionID") as string);
        }
        catch { return false; }
    }

    /// <summary>
    /// Пробный вызов Get-Command Add-AppxPackage — короткий таймаут, т.к. это
    /// диагностика ДО попытки реальной установки, а не сама установка.
    /// false означает, что AppX-стек развёртывания физически отсутствует
    /// (урезанные/дебloat-сборки Windows) — Add-AppxPackage не поможет никакой
    /// перезагрузкой или повтором.
    /// </summary>
    public static async Task<bool> IsAppxStackAvailableAsync(CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = Services.TrustedExecutablePaths.PowerShellExe,
                Arguments              = "-NoProfile -Command \"if (Get-Command Add-AppxPackage -ErrorAction SilentlyContinue) { 'yes' } else { 'no' }\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            string output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return output.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
```

- [ ] **Step 4: Запустить тесты, убедиться, что проходят**

Run: `dotnet test tests/Ven4Tools.Tests --filter "FullyQualifiedName~WindowsEditionInspectorTests"`
Expected: PASS (5/5). (`IsAppxStackAvailableAsync` — реальный PowerShell-вызов, не покрыт юнит-тестом по тому же принципу, что и остальной процесс-based код лаунчера — проверяется вручную в Step 6.)

- [ ] **Step 5: Подключить в `CheckComponentsAutoAsync`/`CheckComponentsInteractiveAsync`**

В `Ven4Tools.Launcher/MainWindow.Components.cs`, метод `CheckComponentsAutoAsync`, ПЕРЕД блоком проверки winget (`AddLog("🔍 Winget...")`) добавить:

```csharp
            bool likelyLtsc = WindowsEditionInspector.IsLikelyLtsc();
            bool appxAvailable = await WindowsEditionInspector.IsAppxStackAvailableAsync(CancellationToken.None);
            if (likelyLtsc)
                AddLog("ℹ️ Похоже, это редакция Windows LTSC/LTSB — winget официально не входит в её состав и может не установиться штатным способом");
            if (!appxAvailable)
                AddLog("⚠️ Компоненты развёртывания AppX недоступны в этой системе — установка winget здесь невозможна независимо от сети");
```

В `CheckComponentsInteractiveAsync`, заменить сообщение перед предложением установить winget (текущий блок `if (!wingetInfo.IsInstalled) { var installResult = MessageBox.Show("Winget ... не установлен!\n\n... Установить winget сейчас?", ...) }`) на:

```csharp
            var wingetInfo = await CheckWingetWithVersionAsync();
            if (!wingetInfo.IsInstalled)
            {
                bool appxAvailable = await WindowsEditionInspector.IsAppxStackAvailableAsync(CancellationToken.None);
                if (!appxAvailable)
                {
                    System.Windows.MessageBox.Show(
                        "Компоненты развёртывания приложений (AppX) недоступны в этой системе — " +
                        "winget здесь установить нельзя. Обычно это встречается на сильно урезанных " +
                        "сборках Windows, где эти компоненты удалены. Переустановка/перезагрузка не поможет.",
                        "Winget недоступен в этой системе", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    string ltscNote = WindowsEditionInspector.IsLikelyLtsc()
                        ? "\n\nПохоже, это редакция Windows LTSC/LTSB — winget официально не входит в её " +
                          "состав, установка может не сработать штатным способом даже после этого шага."
                        : "";
                    var installResult = System.Windows.MessageBox.Show(
                        $"Winget (Windows Package Manager) не установлен!{ltscNote}\n\n" +
                        "Winget необходим для установки большинства приложений.\n\n" +
                        "Установить winget сейчас?",
                        "Требуется winget", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (installResult == MessageBoxResult.Yes)
                    {
                        await InstallWingetAsync();
                        wingetInfo = await CheckWingetWithVersionAsync();
                        AddLog(wingetInfo.IsInstalled
                            ? $"   ✅ Winget {wingetInfo.Version}"
                            : "   ⚠️ Winget всё ещё не найден. Возможно, требуется перезагрузка.");
                    }
                }
            }
```

- [ ] **Step 6: Собрать, вручную проверить на обычной (не-LTSC) машине**

Run: `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений.
Запустить лаунчер вручную, убедиться в логе: `appxAvailable == true`, `likelyLtsc == false` на обычной Windows 11 — новые строки в логе не портят обычный сценарий, ложных срабатываний нет.

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools.Launcher/Services/WindowsEditionInspector.cs Ven4Tools.Launcher/MainWindow.Components.cs tests/Ven4Tools.Tests/WindowsEditionInspectorTests.cs
git commit -m "Лаунчер: честная диагностика LTSC/отсутствия AppX-стека перед установкой winget"
```

---

## Self-Review

**1. Покрытие обсуждённого:**
- Расшифровка ошибок winget (коды/названия) → Task 1. ✅
- «Можно/нельзя вшить установщик» → решение зафиксировано в Global Constraints (не вшивать), ничего не реализуется в эту сторону, как и договорились. ✅
- Загрузка winget с CDN → Task 4. ✅
- Детект архитектуры → Task 3. ✅
- Стоп-события для старых/урезанных Windows (LTSC, отсутствие AppX-стека) → Task 5. ✅
- Попутно найденный автофикс VCLibs (0x80073CF3) из исследования — Task 2, отдельно от простой расшифровки, т.к. это активное действие, а не только диагностика. ✅

**2. Плейсхолдеры:** ARM64 URL в Task 3 помечены явным шагом верификации перед коммитом (реальные имена ассетов не проверялись вживую при написании плана, т.к. это требует захода на живые URL Microsoft/GitHub) — это не «TBD», а конкретное, выполнимое действие с точными адресами для проверки.

**3. Согласованность типов:** `(bool Ok, string? ErrorDetail)` из Task 1 (`RunWingetInstallScriptAsync`) используется без переименования в Task 2 (`scriptResult.Ok`/`scriptResult.ErrorDetail`). `WingetInstallErrorMapper.IsVCLibsConflict` (Task 1) — ровно та же сигнатура, что вызывается в Task 2. `MainWindowWingetUrls.Resolve*Url(bool isArm64)` (Task 3) — согласовано между тестом и реализацией.
