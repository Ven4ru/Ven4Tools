using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace Ven4Tools.Services
{
    /// <summary>Цветовая тема родной панели задач и Пуска.</summary>
    /// <param name="Bar">Заливка панели (#RRGGBB).</param>
    /// <param name="Accent">Акцент Windows: полоски под значками, «Пуск», внутренняя часть ушек.</param>
    /// <param name="Prevalence">Заливать панель и Пуск цветом (иначе — системный тёмный или светлый).</param>
    /// <param name="EarInner">Своя внутренняя часть ушек (по умолчанию — акцент).</param>
    public sealed record TaskbarTheme(string Id, string Title, bool Dark, string Bar, string Accent,
        bool Prevalence, bool Transparency, string? EarInner = null)
    {
        public string Inner => EarInner ?? Accent;
    }

    /// <summary>Доступ к реестру HKCU и оповещению Windows; вынесен интерфейсом ради тестов.</summary>
    public interface IThemeRegistry
    {
        int Build { get; }
        /// <summary>Сырое значение: вид и данные (DWORD — int, двоичное — byte[]); null — значения нет.</summary>
        (RegistryValueKind Kind, object Data)? Read(string key, string name);
        void Write(string key, string name, RegistryValueKind kind, object data);
        void Delete(string key, string name);
        void NotifyColors();
    }

    /// <summary>
    /// Блок 1 «Персонализации», цвет и стиль родной панели задач (дизайн 04.11.2026):
    /// цветовые темы, свой цвет с автозатемнением, панель слева (Windows 10), «Ушки Венчика».
    ///
    /// Цвет пишется в те же значения HKCU, что меняет окно «Параметры → Персонализация → Цвета»
    /// (палитра акцента, DWM, режим, заливка, прозрачность), затем WM_SETTINGCHANGE
    /// «ImmersiveColorSet» — проводник перекрашивает панель сразу (проверено 04.11 на Win11 25H2 и
    /// Win10 22H2). Перед первой правкой каждое значение запоминается вместе с видом и данными
    /// (или его отсутствием) — откат побайтный и не зависит от остальной «Персонализации».
    /// </summary>
    public sealed class TaskbarThemeService
    {
        public const string Accent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        public const string Dwm = @"Software\Microsoft\Windows\DWM";
        public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        public const string StuckRects = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string EarsRunName = "Ven4ToolsEars";
        public const string EarsQuitEvent = @"Local\Ven4ToolsEarsQuit";
        public const string EarsExe = "VenchikEars.exe";
        public const double MinContrast = 4.5;

        /// <summary>Значения, которые меняет тема (и только они).</summary>
        public static readonly (string Key, string Name)[] ThemeValues =
        {
            (Accent, "AccentPalette"), (Accent, "AccentColorMenu"), (Accent, "StartColorMenu"),
            (Dwm, "AccentColor"), (Dwm, "ColorizationColor"), (Dwm, "ColorizationAfterglow"),
            (Personalize, "SystemUsesLightTheme"), (Personalize, "ColorPrevalence"), (Personalize, "EnableTransparency"),
        };

        public static IReadOnlyList<TaskbarTheme> Themes { get; } = new[]
        {
            new TaskbarTheme("venchik",  "Венчик",        true,  "#3E2E22", "#5FE0A0", true,  true, "#EBC3A8"),
            new TaskbarTheme("night",    "Венчик ночной", true,  "#0F3B2C", "#52D18F", true,  true),
            new TaskbarTheme("graphite", "Графит",        true,  "#202020", "#60A5FA", false, true),
            new TaskbarTheme("sea",      "Глубокое море", true,  "#0D3A57", "#38BDF8", true,  true),
            new TaskbarTheme("violet",   "Ночной фиолет", true,  "#2C1F5C", "#A78BFA", true,  true),
            new TaskbarTheme("cherry",   "Вишня",         true,  "#5A1730", "#FB7185", true,  true),
            new TaskbarTheme("ginger",   "Рыжий кот",     true,  "#6A3410", "#FB923C", true,  true),
            new TaskbarTheme("light",    "Светлая",       false, "#EEF1F4", "#2563EB", false, true),
            new TaskbarTheme("midnight", "Полночь",       true,  "#0B1020", "#818CF8", true,  false),
        };

        public sealed record RawValue(bool Exists, string Kind, string Data);

        /// <summary>Состояние раздела: что применено и как было до Ven4Tools.</summary>
        public sealed class State
        {
            public Dictionary<string, RawValue> ColorUndo { get; set; } = new();
            public RawValue? LeftUndo { get; set; }
            public string? ThemeId { get; set; }
            public string? CustomColor { get; set; }
            public bool Ears { get; set; }
            public bool EarsRunExisted { get; set; }
            public string? EarsRunOld { get; set; }
        }

        public static TaskbarThemeService Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "taskbar_theme.json"),
            new WindowsThemeRegistry());

        private readonly string _statePath;
        private readonly IThemeRegistry _reg;
        private readonly object _lock = new();

        public TaskbarThemeService(string statePath, IThemeRegistry reg)
        {
            _statePath = statePath;
            _reg = reg;
        }

        public bool IsWindows11 => _reg.Build >= 22000;

        /// <summary>Родная панель слева есть только в Windows 10 (в 11 с 22H2 её не двигает ничто).</summary>
        public bool CanMoveLeft => !IsWindows11;

        public State Current { get { lock (_lock) return Load(); } }

        public bool HasUndo { get { var s = Current; return s.ColorUndo.Count > 0 || s.LeftUndo != null || s.Ears; } }

        // ── цвет ──────────────────────────────────────────────────────────

        public static (byte R, byte G, byte B) Rgb(string hex)
        {
            var h = hex.TrimStart('#');
            return (Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
        }

        public static string Hex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static double Lin(byte c) { var v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        public static double Luminance((byte R, byte G, byte B) c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

        /// <summary>Контрастность по WCAG (1…21).</summary>
        public static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
        {
            double x = Luminance(a), y = Luminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        /// <summary>Цвет подписей на панели темы: белый на тёмной, почти чёрный на светлой.</summary>
        public static (byte, byte, byte) TextColor(TaskbarTheme t) => t.Dark ? ((byte)255, (byte)255, (byte)255) : ((byte)26, (byte)26, (byte)26);

        /// <summary>Контрастность подписей на заливке темы (для тем без заливки — на системной панели).</summary>
        public static double Readability(TaskbarTheme t) =>
            Contrast(Rgb(t.Prevalence ? t.Bar : (t.Dark ? "#202020" : "#EEEEEE")), TextColor(t));

        /// <summary>Затемнить цвет шагами по 6 %, пока белые подписи не дадут 4,5:1.</summary>
        public static string DarkenToRead(string hex, out bool changed)
        {
            var (r, g, b) = Rgb(hex);
            double R = r, G = g, B = b;
            int n = 0;
            while (Contrast(((byte)R, (byte)G, (byte)B), (255, 255, 255)) < MinContrast && n < 80)
            {
                R *= 0.94; G *= 0.94; B *= 0.94; n++;
            }
            changed = n > 0;
            return Hex(((byte)R, (byte)G, (byte)B));
        }

        /// <summary>Тема из своего цвета: акцент — сам цвет, заливка — он же, затемнённый для читаемости.</summary>
        public static TaskbarTheme FromCustom(string hex, bool transparency, out bool darkened)
        {
            var bar = DarkenToRead(hex, out darkened);
            return new TaskbarTheme("custom", "Свой цвет", true, bar, hex.ToUpperInvariant(), true, transparency);
        }

        /// <summary>Палитра акцента Windows — 8 оттенков RGBA: 0–2 светлее акцента, 3 — акцент, 4–6 — заливка, 7 — серый.</summary>
        public static byte[] BuildPalette(TaskbarTheme t)
        {
            var acc = Rgb(t.Accent); var bar = Rgb(t.Bar);
            static (byte, byte, byte) Mix((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, double k) =>
                ((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
            var w = ((byte)255, (byte)255, (byte)255); var k0 = ((byte)0, (byte)0, (byte)0);
            var pal = new[] { Mix(acc, w, .5), Mix(acc, w, .3), Mix(acc, w, .15), acc, Mix(bar, w, .12), bar, Mix(bar, k0, .2), ((byte)0x58, (byte)0x58, (byte)0x58) };
            return pal.SelectMany(c => new[] { c.Item1, c.Item2, c.Item3, (byte)0 }).ToArray();
        }

        private static int Abgr(string hex) { var (r, g, b) = Rgb(hex); return unchecked((int)(0xFF000000u | (uint)(b << 16 | g << 8 | r))); }
        private static int Argb(string hex, uint a) { var (r, g, b) = Rgb(hex); return unchecked((int)(a << 24 | (uint)(r << 16 | g << 8 | b))); }

        /// <summary>Применить тему. Прежние значения запоминаются только при первой правке.</summary>
        public void ApplyTheme(TaskbarTheme t)
        {
            lock (_lock)
            {
                var st = Load();
                if (st.ColorUndo.Count == 0)
                    foreach (var (k, n) in ThemeValues) st.ColorUndo[k + "\\" + n] = Snapshot(k, n);
                _reg.Write(Accent, "AccentPalette", RegistryValueKind.Binary, BuildPalette(t));
                _reg.Write(Accent, "AccentColorMenu", RegistryValueKind.DWord, Abgr(t.Accent));
                _reg.Write(Accent, "StartColorMenu", RegistryValueKind.DWord, Abgr(t.Bar));
                _reg.Write(Dwm, "AccentColor", RegistryValueKind.DWord, Abgr(t.Accent));
                _reg.Write(Dwm, "ColorizationColor", RegistryValueKind.DWord, Argb(t.Accent, 0xC4));
                _reg.Write(Dwm, "ColorizationAfterglow", RegistryValueKind.DWord, Argb(t.Accent, 0xC4));
                _reg.Write(Personalize, "SystemUsesLightTheme", RegistryValueKind.DWord, t.Dark ? 0 : 1);
                _reg.Write(Personalize, "ColorPrevalence", RegistryValueKind.DWord, t.Prevalence ? 1 : 0);
                _reg.Write(Personalize, "EnableTransparency", RegistryValueKind.DWord, t.Transparency ? 1 : 0);
                st.ThemeId = t.Id;
                st.CustomColor = t.Id == "custom" ? t.Accent : null;
                Save(st);
            }
            _reg.NotifyColors();
        }

        /// <summary>Тема, применённая Ven4Tools (null — не применялась).</summary>
        public TaskbarTheme? AppliedTheme
        {
            get
            {
                var st = Current;
                if (st.ThemeId == "custom" && st.CustomColor != null) return FromCustom(st.CustomColor, true, out _);
                return Themes.FirstOrDefault(t => t.Id == st.ThemeId);
            }
        }

        // ── панель слева (Windows 10) ────────────────────────────────────

        public const int EdgeLeft = 0, EdgeTop = 1, EdgeRight = 2, EdgeBottom = 3;

        /// <summary>Край родной панели в Windows 10: байт 12 значения StuckRects3\Settings (0 слева, 1 сверху, 2 справа, 3 снизу).</summary>
        public int Edge
        {
            get
            {
                var v = _reg.Read(StuckRects, "Settings");
                return v?.Data is byte[] b && b.Length > 12 && b[12] <= 3 ? b[12] : EdgeBottom;
            }
        }

        public bool IsLeft => Edge == EdgeLeft;

        /// <summary>Переставить родную панель к краю (Windows 10). Нужен перезапуск проводника. false — нельзя.
        /// Прежнее значение целиком запоминается при первой правке; возврат к исходному краю — побайтный откат.</summary>
        public bool SetEdge(int edge)
        {
            if (!CanMoveLeft || edge < 0 || edge > 3) return false;
            lock (_lock)
            {
                var v = _reg.Read(StuckRects, "Settings");
                if (v?.Data is not byte[] b || b.Length <= 12) return false;
                var st = Load();
                st.LeftUndo ??= new RawValue(true, v.Value.Kind.ToString(), Convert.ToHexString(b));
                var original = Convert.FromHexString(st.LeftUndo.Data);
                if (original.Length > 12 && original[12] == edge)
                {
                    Restore(StuckRects, "Settings", st.LeftUndo);
                    st.LeftUndo = null;
                }
                else
                {
                    var nb = (byte[])b.Clone(); nb[12] = (byte)edge;
                    _reg.Write(StuckRects, "Settings", RegistryValueKind.Binary, nb);
                }
                Save(st);
            }
            return true;
        }

        public bool SetLeft(bool left) => SetEdge(left ? EdgeLeft : EdgeBottom);

        // ── ушки Венчика ─────────────────────────────────────────────────

        /// <summary>Постоянное место программы ушек (папка клиента меняется при обновлении).</summary>
        public static string EarsInstallPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "Personalization", EarsExe);

        public static string EarsSourcePath => Path.Combine(AppContext.BaseDirectory, "Personalization", "Venchik", EarsExe);

        public static bool EarsAvailable => File.Exists(EarsSourcePath) || File.Exists(EarsInstallPath);

        /// <summary>Включить ушки: скопировать программу, прописать в автозапуск, запустить (прежняя копия закроется сама).</summary>
        public void EnableEars(TaskbarTheme? theme)
        {
            var dst = EarsInstallPath;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            StopEars();
            if (File.Exists(EarsSourcePath)) File.Copy(EarsSourcePath, dst, overwrite: true);
            var args = theme != null ? $"--inner {theme.Inner}" : "";
            var cmd = $"\"{dst}\" {args}".TrimEnd();
            lock (_lock)
            {
                var st = Load();
                if (!st.Ears)
                {
                    var old = _reg.Read(RunKey, EarsRunName);
                    st.EarsRunExisted = old != null;
                    st.EarsRunOld = old?.Data as string;
                }
                _reg.Write(RunKey, EarsRunName, RegistryValueKind.String, cmd);
                st.Ears = true;
                Save(st);
            }
            try { Process.Start(new ProcessStartInfo(dst, args) { UseShellExecute = false }); }
            catch (Exception ex) { AppLogger.Write("⚠️ Ушки Венчика не запустились — " + ex.Message); }
        }

        public void DisableEars()
        {
            StopEars();
            lock (_lock)
            {
                var st = Load();
                if (!st.Ears) return;
                if (st.EarsRunExisted && st.EarsRunOld != null) _reg.Write(RunKey, EarsRunName, RegistryValueKind.String, st.EarsRunOld);
                else _reg.Delete(RunKey, EarsRunName);
                st.Ears = false; st.EarsRunExisted = false; st.EarsRunOld = null;
                Save(st);
            }
        }

        private static void StopEars()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(EarsQuitEvent, out var ev)) using (ev) ev.Set();
                Thread.Sleep(300);
            }
            catch (Exception ex) { AppLogger.Write("⚠️ Ушки Венчика: сигнал закрытия — " + ex.Message); }
        }

        // ── откат ────────────────────────────────────────────────────────

        /// <summary>Вернуть цвет, положение панели и ушки, как было до Ven4Tools. true — нужен перезапуск проводника.</summary>
        public bool RevertAll()
        {
            bool restart = false;
            DisableEars();
            lock (_lock)
            {
                var st = Load();
                foreach (var (path, v) in st.ColorUndo)
                {
                    var i = path.LastIndexOf('\\');
                    Restore(path[..i], path[(i + 1)..], v);
                }
                st.ColorUndo.Clear();
                if (st.LeftUndo != null) { Restore(StuckRects, "Settings", st.LeftUndo); st.LeftUndo = null; restart = true; }
                st.ThemeId = null; st.CustomColor = null;
                Save(st);
            }
            _reg.NotifyColors();
            return restart;
        }

        private RawValue Snapshot(string key, string name)
        {
            var v = _reg.Read(key, name);
            if (v == null) return new RawValue(false, "", "");
            var data = v.Value.Data switch
            {
                byte[] b => Convert.ToHexString(b),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
            return new RawValue(true, v.Value.Kind.ToString(), data);
        }

        private void Restore(string key, string name, RawValue v)
        {
            if (!v.Exists) { _reg.Delete(key, name); return; }
            var kind = Enum.Parse<RegistryValueKind>(v.Kind);
            object data = kind switch
            {
                RegistryValueKind.Binary => Convert.FromHexString(v.Data),
                RegistryValueKind.DWord => int.Parse(v.Data, System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(v.Data, System.Globalization.CultureInfo.InvariantCulture),
                _ => v.Data,
            };
            _reg.Write(key, name, kind, data);
        }

        private State Load()
        {
            try
            {
                if (File.Exists(_statePath))
                    return JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath)) ?? new();
            }
            catch (Exception ex) { AppLogger.Write("⚠️ Тема панели: файл состояния не прочитан — " + ex.Message); }
            return new();
        }

        private void Save(State st)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _statePath, overwrite: true);
        }
    }

    public sealed class WindowsThemeRegistry : IThemeRegistry
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

        public int Build => Environment.OSVersion.Version.Build;

        public (RegistryValueKind Kind, object Data)? Read(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            var v = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return v == null ? null : (k!.GetValueKind(name), v);
        }

        public void Write(string key, string name, RegistryValueKind kind, object data)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
            k.SetValue(name, data, kind);
        }

        public void Delete(string key, string name)
        {
            using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }

        public void NotifyColors()
        {
            foreach (var area in new[] { "ImmersiveColorSet", "WindowsThemeElement" })
                SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, area, 0x0002, 1000, out _);
        }
    }
}
