using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Одна настройка родных панели задач и Пуска.</summary>
    public sealed class TaskbarSetting
    {
        public string Id { get; init; } = "";
        public string Group { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>Раздел HKCU (без «HKCU\»). Для автоскрытия — пусто: оно меняется через SHAppBarMessage.</summary>
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>Значение, которое Windows подразумевает, когда в реестре его нет.</summary>
        public int Default { get; init; }
        public (int Value, string Label)[] Choices { get; init; } = Array.Empty<(int, string)>();
        public bool Windows11Only { get; init; }
        public bool Windows10Only { get; init; }
        /// <summary>Применяется только после перезапуска проводника.</summary>
        public bool NeedsExplorerRestart { get; init; }
    }

    /// <summary>Доступ к системе; вынесен интерфейсом ради тестов.</summary>
    public interface ITaskbarSystem
    {
        int Build { get; }
        int? ReadDword(string key, string name);
        void WriteDword(string key, string name, int value);
        void DeleteValue(string key, string name);
        bool AutoHide { get; set; }
        void NotifyChanged();
        void RestartExplorer();
    }

    /// <summary>
    /// Блок 1 «Панель задач и Пуск: настройка». Только родные элементы Windows и только
    /// документированные пути: пользовательские настройки HKCU (те же, что меняет окно
    /// «Параметры»), автоскрытие через SHAppBarMessage, оповещение WM_SETTINGCHANGE.
    /// Права администратора не нужны, в explorer ничего не внедряется.
    ///
    /// Перед первой правкой каждой настройки запоминается её прежнее состояние (значение
    /// или его отсутствие) в отдельный файл — откат не зависит от остальной «Персонализации».
    /// </summary>
    public sealed class TaskbarStartService
    {
        private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
        private const string Themes = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        public const string AutoHideId = "AutoHide";

        public static IReadOnlyList<TaskbarSetting> All { get; } = new List<TaskbarSetting>
        {
            new() { Id = "Align", Group = "Панель задач", Title = "Положение значков", Key = Adv, Name = "TaskbarAl", Default = 1, Windows11Only = true,
                    Description = "Кнопка «Пуск» и значки — по центру или у левого края, как в Windows 10.",
                    Choices = new[] { (1, "По центру"), (0, "Слева") } },
            new() { Id = "Search11", Group = "Панель задач", Title = "Поиск", Key = Search, Name = "SearchboxTaskbarMode", Default = 2, Windows11Only = true,
                    Description = "Поле поиска на панели занимает много места; можно оставить только значок.",
                    Choices = new[] { (0, "Скрыть"), (1, "Только значок"), (3, "Значок и подпись"), (2, "Поле поиска") } },
            new() { Id = "Search10", Group = "Панель задач", Title = "Поиск", Key = Search, Name = "SearchboxTaskbarMode", Default = 2, Windows10Only = true,
                    Description = "Поле поиска на панели занимает много места; можно оставить только значок.",
                    Choices = new[] { (0, "Скрыть"), (1, "Только значок"), (2, "Поле поиска") } },
            new() { Id = "TaskView", Group = "Панель задач", Title = "Кнопка «Представление задач»", Key = Adv, Name = "ShowTaskViewButton", Default = 1,
                    Description = "Кнопка обзора окон и рабочих столов. Win+Tab работает и без неё.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "Widgets", Group = "Панель задач", Title = "Виджеты", Key = Adv, Name = "TaskbarDa", Default = 1, Windows11Only = true,
                    Description = "Погода и новости слева на панели. В новых сборках Windows эту настройку разрешает менять только окно «Параметры» — тогда откроем его.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "Combine", Group = "Панель задач", Title = "Группировка кнопок", Key = Adv, Name = "TaskbarGlomLevel", Default = 0,
                    Description = "Объединять окна одной программы в одну кнопку или показывать каждое окно с подписью.",
                    Choices = new[] { (0, "Всегда объединять"), (1, "При заполнении"), (2, "Никогда") } },
            new() { Id = "SmallIcons", Group = "Панель задач", Title = "Маленькие значки", Key = Adv, Name = "TaskbarSmallIcons", Default = 0, Windows10Only = true,
                    Description = "Узкая панель задач с маленькими значками.",
                    Choices = new[] { (0, "Обычные"), (1, "Маленькие") } },
            new() { Id = "Seconds", Group = "Панель задач", Title = "Секунды в часах", Key = Adv, Name = "ShowSecondsInSystemClock", Default = 0, NeedsExplorerRestart = true,
                    Description = "Часы на панели показывают секунды. Немного чаще будит процессор — на ноутбуке от батареи лучше без них.",
                    Choices = new[] { (0, "Без секунд"), (1, "С секундами") } },
            new() { Id = AutoHideId, Group = "Панель задач", Title = "Автоскрытие", Default = 0,
                    Description = "Панель прячется за край экрана и появляется, когда к нему подводишь мышь.",
                    Choices = new[] { (0, "Всегда видна"), (1, "Прятать") } },
            new() { Id = "StartLayout", Group = "Пуск", Title = "Раскладка Пуска", Key = Adv, Name = "Start_Layout", Default = 0, Windows11Only = true,
                    Description = "Сколько места отдать закреплённым значкам, а сколько — рекомендациям.",
                    Choices = new[] { (0, "Поровну"), (1, "Больше закреплённых"), (2, "Больше рекомендаций") } },
            new() { Id = "RecentDocs", Group = "Пуск", Title = "Недавние файлы", Key = Adv, Name = "Start_TrackDocs", Default = 1,
                    Description = "Показывать в Пуске и списках переходов недавно открытые файлы.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "RecentApps", Group = "Пуск", Title = "Часто используемые программы", Key = Adv, Name = "Start_TrackProgs", Default = 1,
                    Description = "Пуск запоминает, что запускается чаще, и выводит это наверх.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "Tips", Group = "Пуск", Title = "Советы и реклама в Пуске", Key = Adv, Name = "Start_IrisRecommendations", Default = 1, Windows11Only = true,
                    Description = "Подсказки, рекомендуемые приложения и предложения Microsoft в разделе «Рекомендуем».",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "AccentBar", Group = "Цвет", Title = "Цвет акцента на панели и в Пуске", Key = Themes, Name = "ColorPrevalence", Default = 0,
                    Description = "Панель задач и Пуск окрашиваются в цвет акцента. Работает при тёмном режиме Windows.",
                    Choices = new[] { (0, "Обычные"), (1, "Цвет акцента") } },
            new() { Id = "DarkSystem", Group = "Цвет", Title = "Режим Windows", Key = Themes, Name = "SystemUsesLightTheme", Default = 1,
                    Description = "Светлые или тёмные панель задач, Пуск и центр уведомлений.",
                    Choices = new[] { (1, "Светлый"), (0, "Тёмный") } },
            new() { Id = "Transparency", Group = "Цвет", Title = "Прозрачность", Key = Themes, Name = "EnableTransparency", Default = 1,
                    Description = "Полупрозрачные панель задач и Пуск. Без прозрачности слабым видеокартам чуть легче.",
                    Choices = new[] { (1, "Включена"), (0, "Выключена") } },
            new() { Id = "Copilot", Group = "Панель задач", Title = "Кнопка Copilot", Key = Adv, Name = "ShowCopilotButton", Default = 1, Windows11Only = true,
                    Description = "Кнопка помощника Copilot на панели задач. Сам Copilot остаётся, пропадает только кнопка.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "Chat", Group = "Панель задач", Title = "Кнопка «Чат»", Key = Adv, Name = "TaskbarMn", Default = 1, Windows11Only = true,
                    Description = "Кнопка чата Microsoft Teams в первых выпусках Windows 11. В новых сборках её нет — настройка ничего не сломает.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "EndTask", Group = "Панель задач", Title = "«Завершить задачу» в меню панели", Key = Adv + @"\TaskbarDeveloperSettings", Name = "TaskbarEndTask", Default = 0, Windows11Only = true,
                    Description = "Правый щелчок по зависшей программе на панели — пункт «Завершить задачу», без диспетчера задач.",
                    Choices = new[] { (0, "Нет"), (1, "Есть") } },
            new() { Id = "ShowDesktop", Group = "Панель задач", Title = "Угол «Показать рабочий стол»", Key = Adv, Name = "TaskbarSd", Default = 1, Windows11Only = true,
                    Description = "Щелчок в самый правый угол панели сворачивает все окна.",
                    Choices = new[] { (1, "Включён"), (0, "Выключен") } },
            new() { Id = "MultiMon", Group = "Панель задач", Title = "Панель на всех мониторах", Key = Adv, Name = "MMTaskbarEnabled", Default = 0,
                    Description = "Если мониторов несколько — панель задач на каждом из них.",
                    Choices = new[] { (0, "Только на главном"), (1, "На всех") } },
            new() { Id = "MultiMonMode", Group = "Панель задач", Title = "Кнопки окон на других мониторах", Key = Adv, Name = "MMTaskbarMode", Default = 0,
                    Description = "Где показывать кнопку окна, когда панель есть на всех мониторах.",
                    Choices = new[] { (0, "На всех панелях"), (1, "На главной и там, где окно"), (2, "Только там, где окно") } },
            new() { Id = "TrayAll", Group = "Панель задач", Title = "Значки в области уведомлений", Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer", Name = "EnableAutoTray", Default = 1, Windows10Only = true, NeedsExplorerRestart = true,
                    Description = "Показывать все значки у часов, а не прятать часть под стрелку «^».",
                    Choices = new[] { (1, "Прятать лишние"), (0, "Показывать все") } },
            new() { Id = "AccountNotif", Group = "Пуск", Title = "Напоминания учётной записи в Пуске", Key = Adv, Name = "Start_AccountNotifications", Default = 1, Windows11Only = true,
                    Description = "Значок и уведомления у имени пользователя в Пуске: «войдите в учётную запись Microsoft», «завершите настройку».",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "Bing", Group = "Реклама и подсказки", Title = "Поиск в интернете из Пуска", Key = Search, Name = "BingSearchEnabled", Default = 1,
                    Description = "Пуск ищет не только на компьютере, но и в Bing и открывает результаты в Edge. Можно оставить только поиск по компьютеру.",
                    Choices = new[] { (1, "Компьютер и Bing"), (0, "Только компьютер") } },
            new() { Id = "WebSuggest", Group = "Реклама и подсказки", Title = "Веб-подсказки в поиске Пуска", Key = @"Software\Policies\Microsoft\Windows\Explorer", Name = "DisableSearchBoxSuggestions", Default = 0, NeedsExplorerRestart = true,
                    Description = "Подсказки и «популярное» из интернета в окне поиска. Отключается той же политикой, что у администраторов Windows.",
                    Choices = new[] { (0, "Показывать"), (1, "Не показывать") } },
            new() { Id = "StartSuggestions", Group = "Реклама и подсказки", Title = "Предложения программ в Пуске", Key = Cdm, Name = "SubscribedContent-338388Enabled", Default = 1,
                    Description = "Реклама программ из Microsoft Store среди значков Пуска.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "LockFacts", Group = "Реклама и подсказки", Title = "Факты и советы на экране блокировки", Key = Cdm, Name = "SubscribedContent-338387Enabled", Default = 1,
                    Description = "Подписи, «интересные факты» и реклама поверх картинки экрана блокировки.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "WinTips", Group = "Реклама и подсказки", Title = "Советы при работе с Windows", Key = Cdm, Name = "SubscribedContent-338389Enabled", Default = 1,
                    Description = "Всплывающие советы и «рекомендации» Windows во время работы.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "Welcome", Group = "Реклама и подсказки", Title = "Экран «Добро пожаловать» после обновлений", Key = Cdm, Name = "SubscribedContent-310093Enabled", Default = 1,
                    Description = "Полноэкранный рассказ о новинках Windows после крупного обновления.",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "SettingsSugg", Group = "Реклама и подсказки", Title = "Предложения в «Параметрах»", Key = Cdm, Name = "SubscribedContent-338393Enabled", Default = 1,
                    Description = "Рекомендуемое содержимое и предложения в окне «Параметры».",
                    Choices = new[] { (1, "Показывать"), (0, "Скрыть") } },
            new() { Id = "FinishSetup", Group = "Реклама и подсказки", Title = "«Давайте завершим настройку устройства»", Key = @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", Name = "ScoobeSystemSettingEnabled", Default = 1,
                    Description = "Экран с предложениями служб Microsoft, который Windows показывает после обновлений и при входе.",
                    Choices = new[] { (1, "Предлагать"), (0, "Не предлагать") } },
        };

        /// <summary>Готовые наборы: одна кнопка меняет несколько настроек сразу.</summary>
        public static IReadOnlyList<(string Title, string Description, Dictionary<string, int> Values)> Presets { get; } = new[]
        {
            ("Без рекламы и Bing", "Пуск, поиск, экран блокировки и «Параметры» без рекламы, советов и подсказок Bing.",
                new Dictionary<string, int> { ["Tips"] = 0, ["AccountNotif"] = 0, ["StartSuggestions"] = 0, ["LockFacts"] = 0, ["WinTips"] = 0,
                    ["Welcome"] = 0, ["SettingsSugg"] = 0, ["FinishSetup"] = 0, ["Bing"] = 0, ["WebSuggest"] = 1, ["Copilot"] = 0 }),
            ("Чистая панель", "Поиск значком, без виджетов, Copilot, чата и кнопки представления задач.",
                new Dictionary<string, int> { ["Search11"] = 1, ["Search10"] = 1, ["TaskView"] = 0, ["Widgets"] = 0, ["Copilot"] = 0, ["Chat"] = 0 }),
            ("Как в Windows 10", "Значки слева, поле поиска, окна не объединяются.",
                new Dictionary<string, int> { ["Align"] = 0, ["Search11"] = 2, ["Combine"] = 1 }),
            ("Пуск без лишнего", "Больше закреплённых значков, без советов, недавних файлов и рекламы.",
                new Dictionary<string, int> { ["StartLayout"] = 1, ["Tips"] = 0, ["RecentDocs"] = 0 }),
            ("Тёмная с акцентом", "Тёмные панель и Пуск, окрашенные в цвет акцента.",
                new Dictionary<string, int> { ["DarkSystem"] = 0, ["AccentBar"] = 1, ["Transparency"] = 1 }),
        };

        public sealed class SavedValue
        {
            public bool Existed { get; set; }
            public int Value { get; set; }
        }

        /// <summary>Итог применения: что не получилось и нужен ли перезапуск проводника.</summary>
        public sealed class ApplyResult
        {
            public List<string> Blocked { get; } = new();
            public bool NeedsExplorerRestart { get; set; }
        }

        public static TaskbarStartService Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "taskbar_undo.json"),
            new WindowsTaskbarSystem());

        private readonly string _undoPath;
        private readonly ITaskbarSystem _system;
        private readonly object _lock = new();

        public TaskbarStartService(string undoPath, ITaskbarSystem system)
        {
            _undoPath = undoPath;
            _system = system;
        }

        public bool IsWindows11 => _system.Build >= 22000;

        /// <summary>Настройки, которые есть в этой версии Windows.</summary>
        public IEnumerable<TaskbarSetting> Available =>
            All.Where(s => !(s.Windows11Only && !IsWindows11) && !(s.Windows10Only && IsWindows11));

        public int Current(TaskbarSetting s) =>
            s.Id == AutoHideId ? (_system.AutoHide ? 1 : 0) : _system.ReadDword(s.Key, s.Name) ?? s.Default;

        public bool HasUndo => Load().Count > 0;

        public bool IsChanged(TaskbarSetting s) => Load().ContainsKey(s.Id);

        /// <summary>Применить выбранные значения; неизменившиеся пропускаются.</summary>
        public ApplyResult Apply(IReadOnlyDictionary<string, int> values)
        {
            var result = new ApplyResult();
            lock (_lock)
            {
                var undo = Load();
                foreach (var (id, value) in values)
                {
                    var s = Available.FirstOrDefault(x => x.Id == id);
                    if (s == null || !s.Choices.Any(c => c.Value == value) || Current(s) == value) continue;
                    // Прежнее состояние — только при первой правке, иначе «как было» станет нашим.
                    if (!undo.ContainsKey(s.Id))
                        undo[s.Id] = Snapshot(s);
                    try
                    {
                        Write(s, value);
                        if (s.NeedsExplorerRestart) result.NeedsExplorerRestart = true;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        // Защищённые Windows настройки (например, виджеты в новых сборках).
                        AppLogger.Write($"⚠️ Панель задач: Windows не дала изменить «{s.Title}» — {ex.Message}");
                        result.Blocked.Add(s.Title);
                        undo.Remove(s.Id);
                    }
                }
                Save(undo);
            }
            _system.NotifyChanged();
            return result;
        }

        /// <summary>Вернуть все изменённые настройки, какими они были до Ven4Tools.</summary>
        public ApplyResult RevertAll()
        {
            var result = new ApplyResult();
            lock (_lock)
            {
                var undo = Load();
                foreach (var (id, saved) in undo.ToList())
                {
                    var s = All.FirstOrDefault(x => x.Id == id);
                    if (s == null) { undo.Remove(id); continue; }
                    try
                    {
                        if (s.Id == AutoHideId) _system.AutoHide = saved.Value == 1;
                        else if (saved.Existed) _system.WriteDword(s.Key, s.Name, saved.Value);
                        else _system.DeleteValue(s.Key, s.Name);
                        if (s.NeedsExplorerRestart) result.NeedsExplorerRestart = true;
                        undo.Remove(id);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        AppLogger.Write($"⚠️ Панель задач: не вернулось «{s.Title}» — {ex.Message}");
                        result.Blocked.Add(s.Title);
                    }
                }
                Save(undo);
            }
            _system.NotifyChanged();
            return result;
        }

        public void RestartExplorer() => _system.RestartExplorer();

        private SavedValue Snapshot(TaskbarSetting s)
        {
            if (s.Id == AutoHideId) return new SavedValue { Existed = true, Value = _system.AutoHide ? 1 : 0 };
            var v = _system.ReadDword(s.Key, s.Name);
            return new SavedValue { Existed = v.HasValue, Value = v ?? 0 };
        }

        private void Write(TaskbarSetting s, int value)
        {
            if (s.Id == AutoHideId) _system.AutoHide = value == 1;
            else _system.WriteDword(s.Key, s.Name, value);
        }

        private Dictionary<string, SavedValue> Load()
        {
            try
            {
                if (File.Exists(_undoPath))
                    return JsonSerializer.Deserialize<Dictionary<string, SavedValue>>(File.ReadAllText(_undoPath)) ?? new();
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Панель задач: файл отката не прочитан — " + ex.Message);
            }
            return new();
        }

        private void Save(Dictionary<string, SavedValue> undo)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_undoPath)!);
            var tmp = _undoPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(undo, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _undoPath, overwrite: true);
        }
    }

    public sealed class WindowsTaskbarSystem : ITaskbarSystem
    {
        private const uint ABM_GETSTATE = 0x04;
        private const uint ABM_SETSTATE = 0x0A;
        private const int ABS_AUTOHIDE = 0x01;
        private const int ABS_ALWAYSONTOP = 0x02;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static readonly IntPtr HWND_BROADCAST = new(0xffff);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

        public int Build => Environment.OSVersion.Version.Build;

        public int? ReadDword(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is int v ? v : null;
        }

        public void WriteDword(string key, string name, int value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }

        public void DeleteValue(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }

        public bool AutoHide
        {
            get
            {
                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
                return ((int)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
            }
            set
            {
                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), lParam = new IntPtr(value ? ABS_AUTOHIDE : ABS_ALWAYSONTOP) };
                SHAppBarMessage(ABM_SETSTATE, ref d);
            }
        }

        public void NotifyChanged()
        {
            foreach (var area in new[] { "TraySettings", "ImmersiveColorSet", "WindowsThemeElement" })
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 1000, out _);
        }

        public void RestartExplorer()
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) { AppLogger.Write("⚠️ Проводник не закрылся — " + ex.Message); }
                finally { p.Dispose(); }
            }
            // Если Windows не подняла оболочку сама — запускаем.
            System.Threading.Thread.Sleep(1500);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
    }
}
