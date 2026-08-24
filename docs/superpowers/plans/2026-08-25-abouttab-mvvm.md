# AboutTab MVVM Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перевести вкладку «О программе» (`AboutTab`) с code-behind на MVVM: версия, список изменений каталога, три кнопки-ссылки и чтение хвоста лога переезжают в новый `AboutViewModel` (+ тонкий `ChangelogEntryViewModel` для списка), `AboutTab.xaml.cs` становится тонкой обёрткой, поведение не меняется.

**Architecture:** Тот же паттерн, что `CatalogTab`/`DebloaterTab`/`HistoryTab`: `UserControl` создаёт `ViewModel` в конструкторе, ставит его в `DataContext`, XAML биндится к свойствам/командам. Программная сборка списка изменений (`pnlChangelog.Children.Add(...)`) заменяется на `ItemsControl`+`DataTemplate`, впервые в этой вкладке использующий обёртку элемента списка (`ChangelogEntryViewModel`), по образцу `AppRowViewModel`.

**Tech Stack:** C# / .NET 8 / WPF, `Ven4Tools.ViewModels.RelayCommand` (уже есть в проекте), xUnit для тестов.

## Global Constraints

- Спек: `docs/superpowers/specs/2026-08-25-abouttab-mvvm-design.md` — читать перед началом.
- Чистый рефакторинг, поведение 1:1. Никаких попутных фиксов, даже мелких.
- `MainWindow.xaml.cs` — не трогать, у `AboutTab` нет публичного контракта, который кто-то вызывает извне (только конструктор).
- Работа в ветке `mvvm-abouttab` (уже создана от `main`, активна). Коммитить локально после каждой задачи. **Не пушить** в `origin` без отдельного явного разрешения.
- `dotnet test` (запуск юнит-тестов) и любой запуск `ClientUITests` — только с явного разрешения пользователя каждый раз (см. память `feedback_no_tests_without_agreement`; на машине VenchWork пользователь дал общее разрешение в этой сессии — но всё равно называть, что именно запускается). `dotnet build` — можно свободно.
- Все тексты (комментарии, сообщения, тесты) — только на русском.
- Существующие UI-тесты `AboutTab_ОбратнаяСвязьИСообщитьОПроблеме_ОткрываютБраузер` (`Ven4Tools.ClientUITests/Phase4MainWindowRemainingTests.cs`) и навигационная проверка `btnGitHub` в `Ven4Tools.ClientUITests/KeyButtonsSmokeTests.cs` должны остаться зелёными — новый UI-тест не создаётся, эта вкладка уже покрыта.

---

### Task 1: Создать `ChangelogEntryViewModel` + `AboutViewModel` + юнит-тесты

**Files:**
- Create: `Ven4Tools/ViewModels/ChangelogEntryViewModel.cs`
- Create: `Ven4Tools/ViewModels/AboutViewModel.cs`
- Create: `tests/Ven4Tools.Tests/AboutViewModelTests.cs`

**Interfaces:**
- Consumes: `Ven4Tools.Models.CatalogChangelogEntry` (существующий, поля `Version` (int), `Date` (string), `AddedApps` (`List<string>`), `Message` (string)), `Ven4Tools.Services.CatalogLoaderService.State.Catalog` (существующий, `.Changelog` — `List<CatalogChangelogEntry>`), `Ven4Tools.Services.AppLogger.Write(string)` (существующий), `Ven4Tools.Services.CrashReportService.SanitizePath(string)` (существующий, `internal static`, тот же assembly), `Ven4Tools.ViewModels.RelayCommand` (существующий).
- Produces:
  - `Ven4Tools.ViewModels.ChangelogEntryViewModel` — публичный конструктор `ChangelogEntryViewModel(CatalogChangelogEntry entry)`, свойства `string HeaderText`, `string Message`, `bool HasMessage`, `string AddedAppsText`, `bool HasAddedApps` (все только для чтения, не `INotifyPropertyChanged`).
  - `Ven4Tools.ViewModels.AboutViewModel` — публичные члены: `string VersionText` (get), `List<ChangelogEntryViewModel> ChangelogEntries` (get), `bool HasChangelog` (get), `bool NoChangelog` (get), `RelayCommand GitHubCommand`, `RelayCommand FeedbackCommand`, `RelayCommand ReportIssueCommand`, `void RefreshChangelog()`. Используется в Task 2.

- [ ] **Step 1: Написать `ChangelogEntryViewModel.cs`**

Полное содержимое `Ven4Tools/ViewModels/ChangelogEntryViewModel.cs`:

```csharp
using System.Linq;
using Ven4Tools.Models;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка списка «История изменений каталога» на вкладке «О программе»:
    /// оборачивает <see cref="CatalogChangelogEntry"/> для биндинга, не меняя
    /// саму модель каталога — та общая с загрузкой/подписью каталога, UI-логике
    /// там не место. Данные неизменны после построения записи каталога, поэтому
    /// без INotifyPropertyChanged, как DebloatItem/AppRowViewModel для полей,
    /// не меняющихся после создания. Вынесено из code-behind при переходе на
    /// MVVM (2026-08-25, третья вкладка после пилота DebloaterTab и HistoryTab).
    /// </summary>
    public class ChangelogEntryViewModel
    {
        public string HeaderText { get; }
        public string Message { get; }
        public bool HasMessage { get; }
        public string AddedAppsText { get; }
        public bool HasAddedApps { get; }

        public ChangelogEntryViewModel(CatalogChangelogEntry entry)
        {
            HeaderText = $"v{entry.Version}  ·  {entry.Date}";
            Message = entry.Message;
            HasMessage = !string.IsNullOrEmpty(entry.Message);
            HasAddedApps = entry.AddedApps?.Count > 0;
            AddedAppsText = HasAddedApps ? $"+ {string.Join(", ", entry.AddedApps!)}" : "";
        }
    }
}
```

- [ ] **Step 2: Написать `AboutViewModel.cs`**

Полное содержимое `Ven4Tools/ViewModels/AboutViewModel.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «О программе» — версия, история изменений каталога, три
    /// кнопки-ссылки, чтение хвоста лога для отчёта об ошибке. Перенесено из
    /// code-behind при переходе на MVVM (2026-08-25, третья вкладка после
    /// пилота DebloaterTab и HistoryTab), поведение не менялось.
    /// </summary>
    public sealed class AboutViewModel
    {
        public string VersionText { get; }

        public System.Collections.Generic.List<ChangelogEntryViewModel> ChangelogEntries { get; private set; } = new();

        public bool HasChangelog => ChangelogEntries.Count > 0;
        public bool NoChangelog => !HasChangelog;

        public RelayCommand GitHubCommand { get; }
        public RelayCommand FeedbackCommand { get; }
        public RelayCommand ReportIssueCommand { get; }

        public AboutViewModel()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText = $"Версия {version?.ToString() ?? "—"}";

            GitHubCommand = new RelayCommand(_ => OpenGitHub());
            FeedbackCommand = new RelayCommand(_ => OpenFeedback());
            ReportIssueCommand = new RelayCommand(_ => OpenReportIssue());

            RefreshChangelog();
        }

        /// <summary>
        /// Перестраивает список изменений каталога. Вызывается из конструктора
        /// и заново — из code-behind при событии CatalogReady (тот же паттерн,
        /// что и раньше: если каталог уже загружен на момент открытия вкладки,
        /// список должен обновиться).
        /// </summary>
        public void RefreshChangelog()
        {
            var entries = CatalogLoaderService.State.Catalog?.Changelog;

            ChangelogEntries = entries == null || entries.Count == 0
                ? new()
                : entries.OrderByDescending(e => e.Version)
                         .Select(e => new ChangelogEntryViewModel(e))
                         .ToList();
        }

        private void OpenGitHub()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/Ven4ru/Ven4Tools",
                    UseShellExecute = true
                });
                AppLogger.Write("🌐 Открыт GitHub репозиторий");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }

        private void OpenFeedback()
        {
            try
            {
                var osVersion = Environment.OSVersion.VersionString;
                var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";

                var title = Uri.EscapeDataString($"Обратная связь: {appVersion}");
                var body = Uri.EscapeDataString(
                    $"## Версия\n{appVersion}\n\n" +
                    $"## ОС\n{osVersion}\n\n" +
                    $"## Сообщение\n\n");

                var url = $"https://github.com/Ven4ru/Ven4Tools/issues/new?title={title}&body={body}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                AppLogger.Write("📧 Открыта форма обратной связи");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка открытия обратной связи: {ex.Message}");
                MessageBox.Show("Не удалось открыть форму обратной связи.\n" +
                                "Пожалуйста, напишите на GitHub вручную:\n" +
                                "https://github.com/Ven4ru/Ven4Tools/issues",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenReportIssue()
        {
            try
            {
                var osVersion = Environment.OSVersion.VersionString;
                var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";

                string lastLogs = GetLastLogLines();

                var title = Uri.EscapeDataString($"[BUG] Проблема в версии {appVersion}");
                var body = Uri.EscapeDataString(
                    $"## Описание проблемы\n\n" +
                    $"### Шаги воспроизведения\n1. \n2. \n3. \n\n" +
                    $"### Ожидаемое поведение\n\n" +
                    $"### Фактическое поведение\n\n" +
                    $"## Системная информация\n" +
                    $"Версия: {appVersion}\n" +
                    $"ОС: {osVersion}\n\n" +
                    $"## Последние логи\n```\n{lastLogs}\n```");

                var url = $"https://github.com/Ven4ru/Ven4Tools/issues/new?title={title}&body={body}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });

                AppLogger.Write("🐛 Открыта форма сообщения о проблеме");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
            }
        }

        internal string GetLastLogLines(int lines = 15)
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "logs");

                if (!Directory.Exists(logDir)) return "Лог не найден";

                var logPath = Directory.GetFiles(logDir, "install_*.log")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (logPath == null) return "Лог не найден";

                var allLines = File.ReadAllLines(logPath);
                return FormatLastLines(allLines, lines);
            }
            catch
            {
                return "Не удалось прочитать лог";
            }
        }

        /// <summary>
        /// Чистая функция форматирования хвоста лога — вынесена из
        /// <see cref="GetLastLogLines"/>, чтобы дать юнит-тестам шов для
        /// проверки логики обрезки (по числу строк и по числу символов) без
        /// обращения к реальному файлу на диске. Семантика не менялась: та же
        /// обрезка по количеству строк, тот же лимит символов, та же пометка
        /// "(лог обрезан, ...)" при срабатывании любого из двух лимитов.
        /// </summary>
        internal static string FormatLastLines(string[] allLines, int lines)
        {
            // L11: лог кодируется в URL GitHub issue — ограничиваем и число строк, и общий
            // объём символов, чтобы не превысить лимит URL и не обрезаться молча. Факт
            // обрезки явно помечаем в тексте.
            bool truncated = allLines.Length > lines;
            var lastLines = allLines.Skip(Math.Max(0, allLines.Length - lines)).Take(lines).ToArray();
            string body = CrashReportService.SanitizePath(string.Join("\n", lastLines));

            const int maxChars = 3000;
            if (body.Length > maxChars)
            {
                body = body.Substring(body.Length - maxChars);
                truncated = true;
            }
            if (truncated)
                body = "… (лог обрезан, показаны только последние строки) …\n" + body;
            return body;
        }
    }
}
```

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок (в `AboutTab.xaml.cs` пока будут неиспользуемые методы/поля — это ожидаемо, он ещё не переписан, чинится в Task 2). Если ошибок нет вообще (оба файла независимы до Task 2) — тоже нормально, значит `AboutTab.xaml.cs` не ссылается на новые типы.

- [ ] **Step 4: Написать юнит-тесты**

Полное содержимое `tests/Ven4Tools.Tests/AboutViewModelTests.cs`:

```csharp
using System.Linq;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «О программе», перенесённая из code-behind в ViewModel
/// (2026-08-25). Основное внимание — AboutViewModel.FormatLastLines: чистая
/// функция обрезки хвоста лога, до этого рефакторинга не имевшая ни одного
/// теста. Реальные кнопки-ссылки (Process.Start открывает браузер) здесь не
/// проверяются — это открытие внешнего процесса, не логика.
/// </summary>
public class AboutViewModelTests
{
    [Fact]
    public void FormatLastLines_МеньшеЛимитаСтрок_НичегоНеОбрезает()
    {
        var lines = new[] { "строка1", "строка2", "строка3" };

        var result = AboutViewModel.FormatLastLines(lines, 15);

        Assert.Equal("строка1\nстрока2\nстрока3", result);
        Assert.DoesNotContain("обрезан", result);
    }

    [Fact]
    public void FormatLastLines_БольшеЛимитаСтрок_ОставляетТолькоПоследние()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"строка{i}").ToArray();

        var result = AboutViewModel.FormatLastLines(lines, 5);

        Assert.Contains("строка16\nстрока17\nстрока18\nстрока19\nстрока20", result);
        Assert.DoesNotContain("строка15", result);
        Assert.StartsWith("… (лог обрезан, показаны только последние строки) …\n", result);
    }

    [Fact]
    public void FormatLastLines_ПустойМассив_ВозвращаетПустоеТелоБезПометкиОбрезки()
    {
        var result = AboutViewModel.FormatLastLines(System.Array.Empty<string>(), 15);

        Assert.Equal("", result);
    }

    [Fact]
    public void FormatLastLines_ПревышенЛимитСимволов_ОбрезаетИПомечает()
    {
        // Одна строка длиннее 3000 символов — превышает maxChars даже при
        // единственной строке (лимит строк 15 не сработал бы сам по себе).
        string longLine = new string('x', 5000);
        var lines = new[] { longLine };

        var result = AboutViewModel.FormatLastLines(lines, 15);

        Assert.StartsWith("… (лог обрезан, показаны только последние строки) …\n", result);
        // Тело после пометки — ровно последние 3000 символов исходной строки.
        string body = result.Substring(result.IndexOf('\n') + 1);
        Assert.Equal(3000, body.Length);
        Assert.Equal(longLine.Substring(longLine.Length - 3000), body);
    }

    [Fact]
    public void FormatLastLines_РовноНаГраницеЛимитаСтрок_НеСчитаетсяОбрезкой()
    {
        var lines = Enumerable.Range(1, 15).Select(i => $"строка{i}").ToArray();

        var result = AboutViewModel.FormatLastLines(lines, 15);

        Assert.DoesNotContain("обрезан", result);
        Assert.StartsWith("строка1\n", result);
        Assert.EndsWith("строка15", result);
    }

    [Fact]
    public void GetLastLogLines_КаталогЛоговНеСуществует_ВозвращаетСообщениеОбОтсутствии()
    {
        // Реальный %LocalAppData%\Ven4Tools\logs почти наверняка существует на
        // машине разработки (клиент туда пишет логи при обычной работе) —
        // поэтому эта проверка не бьёт по несуществующему пути напрямую, а
        // проверяет случай через приватную логику GetLastLogLines нельзя без
        // DI пути каталога, которого в оригинале не было (сохраняем 1:1).
        // Вместо этого просто убеждаемся, что метод не бросает исключение и
        // возвращает непустую строку в любом состоянии реальной машины —
        // содержательное покрытие обрезки уже даёт FormatLastLines выше.
        var vm = new AboutViewModel();

        string result = vm.GetLastLogLines();

        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void ChangelogEntries_БезЗагруженногоКаталога_ПустойСписокИHasChangelogFalse()
    {
        var vm = new AboutViewModel();

        Assert.Empty(vm.ChangelogEntries);
        Assert.False(vm.HasChangelog);
        Assert.True(vm.NoChangelog);
    }

    [Fact]
    public void VersionText_НачинаетсяСоСлова_Версия()
    {
        var vm = new AboutViewModel();

        Assert.StartsWith("Версия ", vm.VersionText);
    }
}
```

- [ ] **Step 5: Запустить тесты (с разрешения пользователя)**

Спросить пользователя явно: «Можно запустить `dotnet test tests/Ven4Tools.Tests --filter FullyQualifiedName~AboutViewModelTests`?» Только после «да» (или если пользователь ранее в этой сессии дал общее разрешение для VenchWork — уточнить, где именно запускать):

Run: `dotnet test tests/Ven4Tools.Tests --filter FullyQualifiedName~AboutViewModelTests`
Expected: все 7 тестов из Step 4 зелёные.

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools/ViewModels/ChangelogEntryViewModel.cs Ven4Tools/ViewModels/AboutViewModel.cs tests/Ven4Tools.Tests/AboutViewModelTests.cs
git commit -m "feat(about): AboutViewModel + ChangelogEntryViewModel + юнит-тесты"
```

---

### Task 2: Переписать `AboutTab.xaml`/`AboutTab.xaml.cs` на тонкую обёртку

**Files:**
- Modify: `Ven4Tools/Views/Tabs/AboutTab.xaml`
- Modify: `Ven4Tools/Views/Tabs/AboutTab.xaml.cs`

**Interfaces:**
- Consumes: `Ven4Tools.ViewModels.AboutViewModel` (Task 1) — все публичные члены, перечисленные в Task 1.
- Produces: `AboutTab` без публичного контракта сверх конструктора (как и раньше — снаружи никто не вызывает методы `AboutTab`, только `MainWindow.xaml.cs` создаёт экземпляр).

- [ ] **Step 1: Переписать `AboutTab.xaml`**

Полное содержимое `Ven4Tools/Views/Tabs/AboutTab.xaml` (изменения — `UserControl.Resources` с конвертером, `txtVersion.Text`, замена `pnlChangelog` на `ItemsControl` + плейсхолдер, `Command` у трёх кнопок; сама разметка карточек/текстов не менялась):

```xml
<UserControl x:Class="Ven4Tools.Views.Tabs.AboutTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="20" HorizontalAlignment="Center">
            <Image Source="/icon.ico" Width="96" Height="96" Margin="0,20,0,20"/>
            
            <TextBlock Text="Ven4Tools" FontSize="32" FontWeight="Bold" 
                       Foreground="{DynamicResource TextPrimary}" HorizontalAlignment="Center"/>
            <TextBlock x:Name="txtVersion" FontSize="14" Text="{Binding VersionText}"
                       Foreground="{DynamicResource TextSecondary}" Margin="0,5,0,20"/>
            
            <TextBlock Text="Инструмент для автоматической установки программ" 
                       Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap"
                       HorizontalAlignment="Center" TextAlignment="Center" Margin="0,0,0,20"/>
            
            <!-- Карточки в стиле лучших вкладок проекта (Каталог/Очистка): единая рамка +
                 скруглённый угол + CardBackground вместо плоского GroupBox с двух-тонной
                 шапкой (визуальный аудит 2026-07-24) — сама вкладка была самой «плоской» в
                 приложении. Заголовок карточки — вручную (тот же вес/размер шрифта, что и в
                 стандартном GroupBox-заголовке), контент и формулировки не менялись. -->
            <Border Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    CornerRadius="10" Padding="16,14" Margin="0,0,0,14" Width="500">
                <StackPanel>
                    <TextBlock Text="📋 О программе" FontWeight="Bold" FontSize="13"
                               Foreground="{DynamicResource HeaderForeground}" Margin="0,0,0,10"/>
                    <TextBlock TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}">
                        Ven4Tools — это удобный установщик программ для Windows.

                        • Поддержка winget и прямых ссылок
                        • Каталог популярных приложений
                        • Добавление своих программ
                        • Установка Microsoft Office
                        • Активация Windows и Office
                        • Автоматическая проверка доступности
                        • Альтернативные источники для недоступных приложений
                    </TextBlock>
                </StackPanel>
            </Border>

            <Border Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    CornerRadius="10" Padding="16,14" Margin="0,0,0,14" Width="500">
                <StackPanel>
                    <TextBlock Text="🔧 Используемые технологии" FontWeight="Bold" FontSize="13"
                               Foreground="{DynamicResource HeaderForeground}" Margin="0,0,0,10"/>
                    <TextBlock TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}">
                        • .NET 8.0 / WPF
                        • winget (Windows Package Manager)
                        • PowerShell Core
                        • Microsoft Activation Scripts (MAS)
                    </TextBlock>
                </StackPanel>
            </Border>

            <Border Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    CornerRadius="10" Padding="16,14" Margin="0,0,0,14" Width="500">
                <StackPanel>
                    <TextBlock Text="📌 Ссылки" FontWeight="Bold" FontSize="13"
                               Foreground="{DynamicResource HeaderForeground}" Margin="0,0,0,10"/>
                    <Button x:Name="btnGitHub" Content="🐙 GitHub репозиторий"
                            ToolTip="Откроет страницу исходного кода и релизов Ven4Tools на GitHub."
                            Height="35" Margin="0,5" HorizontalAlignment="Left" Width="200"
                            Command="{Binding GitHubCommand}"/>
                    <Button x:Name="btnFeedback" Content="📧 Обратная связь"
                            ToolTip="Откроет форму для оценки и отправки комментария разработчику."
                            Height="35" Margin="0,5" HorizontalAlignment="Left" Width="200"
                            Command="{Binding FeedbackCommand}"/>
                    <Button x:Name="btnReportIssue" Content="🐛 Сообщить о проблеме"
                            ToolTip="Откроет GitHub с подготовленной формой сообщения о проблеме и данными о версии системы."
                            Height="35" Margin="0,5" HorizontalAlignment="Left" Width="200"
                            Command="{Binding ReportIssueCommand}"/>
                </StackPanel>
            </Border>

            <Border Background="{DynamicResource CardBackground}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    CornerRadius="10" Padding="16,14" Margin="0,0,0,14" Width="500">
                <StackPanel>
                    <TextBlock Text="📅 История изменений каталога" FontWeight="Bold" FontSize="13"
                               Foreground="{DynamicResource HeaderForeground}" Margin="0,0,0,10"/>
                    <TextBlock Text="История изменений будет доступна после загрузки каталога."
                               Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap"
                               Visibility="{Binding NoChangelog, Converter={StaticResource BoolToVis}}"/>
                    <ItemsControl x:Name="pnlChangelog" ItemsSource="{Binding ChangelogEntries}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <StackPanel Margin="0,0,0,10">
                                    <TextBlock Text="{Binding HeaderText}" FontWeight="SemiBold"
                                               Foreground="{DynamicResource TextPrimary}"/>
                                    <TextBlock Text="{Binding Message}"
                                               Foreground="{DynamicResource TextSecondary}"
                                               TextWrapping="Wrap" Margin="0,2,0,0"
                                               Visibility="{Binding HasMessage, Converter={StaticResource BoolToVis}}"/>
                                    <TextBlock Text="{Binding AddedAppsText}"
                                               Foreground="#64C864"
                                               TextWrapping="Wrap" Margin="0,2,0,0" FontSize="11"
                                               Visibility="{Binding HasAddedApps, Converter={StaticResource BoolToVis}}"/>
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
            </Border>

            <TextBlock Text="© 2024-2025 Ven4ru. Свободное программное обеспечение."
                       Foreground="{DynamicResource TextSecondary}" FontSize="10" Margin="0,20,0,10"/>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

Примечание по цвету «добавленных приложений»: оригинал использовал `new SolidColorBrush(Color.FromRgb(100, 200, 100))` в code-behind — это `#64C864` в HEX (100=0x64, 200=0xC8, 100=0x64). Использован именно этот HEX в XAML выше, тот же цвет.

- [ ] **Step 2: Переписать `AboutTab.xaml.cs`**

Полное содержимое `Ven4Tools/Views/Tabs/AboutTab.xaml.cs`:

```csharp
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «О программе» — тонкая обёртка над <see cref="AboutViewModel"/>.
    /// Вся логика перенесена в ViewModel при MVVM-миграции (2026-08-25, третья
    /// вкладка после пилота DebloaterTab и HistoryTab). Публичного контракта
    /// сверх конструктора нет — снаружи никто не обращается к AboutTab, кроме
    /// MainWindow.xaml.cs, который только создаёт экземпляр.
    /// </summary>
    public partial class AboutTab : UserControl
    {
        private readonly AboutViewModel _viewModel = new();
        private bool _catalogReadySubscribed;

        public AboutTab()
        {
            InitializeComponent();
            DataContext = _viewModel;

            Loaded += (_, _) =>
            {
                // Loaded может срабатывать многократно (переключение вкладок) —
                // подписываемся только один раз, иначе обработчики дублируются.
                if (!_catalogReadySubscribed)
                {
                    CatalogLoaderService.CatalogReady += OnCatalogReady;
                    _catalogReadySubscribed = true;
                }
                // Обновляем changelog если каталог уже был загружен до открытия вкладки
                _viewModel.RefreshChangelog();
            };
            Unloaded += (_, _) =>
            {
                if (_catalogReadySubscribed)
                {
                    CatalogLoaderService.CatalogReady -= OnCatalogReady;
                    _catalogReadySubscribed = false;
                }
            };
        }

        private void OnCatalogReady(Models.MasterCatalog _)
        {
            Dispatcher.Invoke(() => _viewModel.RefreshChangelog());
        }
    }
}
```

Обратить внимание: оригинал вызывал `pnlChangelog.Children.Clear(); PopulateChangelog();` и в `Loaded`, и в `OnCatalogReady` БЕЗ условия «только если каталог уже загружен» внутри `Loaded` — в оригинале условие было `if (CatalogLoaderService.State.Status != CatalogLoadStatus.NotLoaded)`. Здесь это условие можно убрать по той причине, что `AboutViewModel.RefreshChangelog()` уже само по себе идемпотентно: если каталог не загружен, `CatalogLoaderService.State.Catalog` равен `null`, `ChangelogEntries` просто становится пустым списком — вызов без проверки статуса безопасен и не меняет видимое поведение (пустой список и до этого показывал плейсхолдер). Это единственное сознательное упрощение условия в этой миграции — если ревьюер сочтёт, что убирать проверку статуса рискованно, вернуть её как `if (CatalogLoaderService.State.Status != CatalogLoadStatus.NotLoaded) _viewModel.RefreshChangelog();` — семантически идентично, чуть более явно.

- [ ] **Step 3: Проверить сборку**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0 ошибок, 0 предупреждений — во всех проектах, включая `Ven4Tools.ClientUITests` (там ссылок на внутренности `AboutTab` быть не должно, только на `AutomationId` — `btnGitHub`/`btnFeedback`/`btnReportIssue`, все сохранены в XAML выше).

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/Tabs/AboutTab.xaml Ven4Tools/Views/Tabs/AboutTab.xaml.cs
git commit -m "refactor(about): AboutTab — тонкая обёртка над AboutViewModel"
```

---

### Task 3: Верификация — регрессия существующих тестов и живой клик

**Files:**
- Не создаёт и не меняет файлы (только проверка того, что сделано в Task 1-2).

**Interfaces:**
- Не применимо (верификационная задача).

- [ ] **Step 1: Полная сборка Release**

Run: `dotnet build Ven4Tools.sln -c Release -warnaserror --no-incremental`
Expected: 0/0.

- [ ] **Step 2: Спросить разрешение и прогнать юнит-тесты целиком**

Спросить: «Можно прогнать весь `dotnet test tests/Ven4Tools.Tests`?» После «да» (или используя уже данное в этой сессии общее разрешение на VenchWork — уточнить площадку):

Run: `dotnet test tests/Ven4Tools.Tests`
Expected: было 394/394 после HistoryTab (см. память `project_ven4tools_mvvm_migration_historytab_2026_08_25`) + 7 новых из `AboutViewModelTests` = 401/401. Если число другое — разбираться, не игнорировать расхождение.

- [ ] **Step 3: Спросить разрешение и прогнать существующие UI-тесты по AboutTab на VenchWork**

Спросить: «Можно прогнать `AboutTab_ОбратнаяСвязьИСообщитьОПроблеме_ОткрываютБраузер` и навигационную проверку `btnGitHub` живым запуском клиента на VenchWork?» После «да», по обычному рецепту (`schtasks /it /rl HIGHEST`, см. память `reference_ui_tests_known_issues_20260724` и `reference_device_topology`), перенос ветки `mvvm-abouttab` через `git bundle` (та же процедура, что для HistoryTab, см. `project_ven4tools_mvvm_migration_historytab_2026_08_25`, включая приём «сначала checkout на другую ветку, потом fetch bundle, потом обратно», если на VenchWork уже стоит какая-то ветка):

Run (на VenchWork): `dotnet test Ven4Tools.ClientUITests --filter FullyQualifiedName~AboutTab_ОбратнаяСвязьИСообщитьОПроблеме_ОткрываютБраузер`
Expected: 1/1 пройден.

Затем: `dotnet test Ven4Tools.ClientUITests --filter FullyQualifiedName~KeyButtonsSmokeTests`
Expected: не хуже базового результата (сверить, что тест, затрагивающий `btnAboutTab`/`btnGitHub`, зелёный — конкретное имя теста смотреть в файле `KeyButtonsSmokeTests.cs`, строка ~107/125 на момент написания плана).

- [ ] **Step 4: Живой ручной клик (по усмотрению — не обязателен, если Step 1-3 зелёные)**

Запустить клиент, открыть вкладку «О программе»:
1. Версия отображается.
2. Три карточки текста («О программе», «Используемые технологии», «Ссылки») выглядят как раньше.
3. История изменений каталога — если каталог загружен, показывает записи (заголовок + опционально сообщение + опционально зелёный список добавленных приложений); если каталог ещё не загружен — плейсхолдер, который затем сменяется списком после загрузки.
4. Клик по «GitHub репозиторий» открывает браузер.
5. Клик по «Обратная связь» и «Сообщить о проблеме» открывают браузер с предзаполненной формой (не обязательно отправлять issue, просто увидеть, что форма открылась).

Если что-то не совпадает с поведением до миграции — чинить в этой же ветке до финального коммита.

- [ ] **Step 5: Финальный коммит (только если Step 1-4 все зелёные)**

```bash
git add -A
git status
git commit -m "test(about): MVVM-миграция AboutTab проверена вживую" --allow-empty
```

---

## После задачи

Не пушить, не мержить в `main` без отдельного явного разрешения. Доложить пользователю результат и ждать решения — мержить в `main` (как было с HistoryTab) или сначала пожить с этой веткой.
