using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ven4Start;

/// <summary>
/// Цвета меню — из темы панели, применённой в Ven4Tools («Персонализация», блок 1): Пуск и панель одного стиля.
/// Таблица совпадает с TaskbarThemeService.Themes клиента. Тема не применялась — «Венчик».
/// </summary>
internal sealed record StartTheme(string Id, bool Dark, string Bar, string Accent, string Inner)
{
    private static readonly StartTheme[] All =
    {
        new("venchik",  true,  "#3E2E22", "#5FE0A0", "#EBC3A8"),
        new("night",    true,  "#0F3B2C", "#52D18F", "#52D18F"),
        new("graphite", true,  "#202020", "#60A5FA", "#60A5FA"),
        new("sea",      true,  "#0D3A57", "#38BDF8", "#38BDF8"),
        new("violet",   true,  "#2C1F5C", "#A78BFA", "#A78BFA"),
        new("cherry",   true,  "#5A1730", "#FB7185", "#FB7185"),
        new("ginger",   true,  "#6A3410", "#FB923C", "#FB923C"),
        new("light",    false, "#F3F4F6", "#2563EB", "#2563EB"),
        new("midnight", true,  "#0B1020", "#818CF8", "#818CF8"),
    };

    public static StartTheme Current()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "taskbar_theme.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var id = doc.RootElement.TryGetProperty("ThemeId", out var t) ? t.GetString() : null;
                if (id == "custom" && doc.RootElement.TryGetProperty("CustomColor", out var c) && c.GetString() is { } hex)
                    return new("custom", true, Darken(hex), hex, hex);
                foreach (var th in All) if (th.Id == id) return th;
            }
        }
        catch { /* нет файла или он битый — тема по умолчанию */ }
        return All[0];
    }

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Так же, как клиент: затемнять шагами по 6 %, пока белый текст не даст 4,5:1.</summary>
    private static string Darken(string hex)
    {
        var c = C(hex); double r = c.R, g = c.G, b = c.B;
        static double L(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        for (int i = 0; i < 80 && 1.05 / (0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b) + 0.05) < 4.5; i++) { r *= .94; g *= .94; b *= .94; }
        return $"#{(byte)r:X2}{(byte)g:X2}{(byte)b:X2}";
    }

    public Color Bg => Shade(C(Bar), Dark ? 0.9 : 1.0);
    public Color Panel => Mix(C(Bar), Dark ? Colors.White : Colors.Black, Dark ? 0.08 : 0.05);
    public Color Hover => Mix(C(Bar), Dark ? Colors.White : Colors.Black, Dark ? 0.15 : 0.09);
    public Color Fg => Dark ? Color.FromRgb(0xF4, 0xF6, 0xF8) : Color.FromRgb(0x1A, 0x1C, 0x22);
    public Color FgDim => Dark ? Color.FromRgb(0xB4, 0xBA, 0xC4) : Color.FromRgb(0x5A, 0x60, 0x6B);

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    private static Color Shade(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    /// <summary>Значки слева — меню тоже слева (как в Windows 10), иначе по центру.</summary>
    public static bool StartAtLeft()
    {
        if (Environment.OSVersion.Version.Build < 22000) return true;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return k?.GetValue("TaskbarAl") is int v && v == 0;
        }
        catch { return false; }
    }
}
