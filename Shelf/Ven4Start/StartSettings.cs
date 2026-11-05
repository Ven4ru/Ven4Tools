using System;
using System.IO;
using System.Text.Json;

namespace Ven4Start;

/// <summary>
/// Настройки меню (%LOCALAPPDATA%\Ven4Start\settings.json): размер и какие разделы показывать —
/// главные жалобы на родной Пуск Windows 11: «огромный, размер не меняется, лишние разделы».
/// </summary>
internal sealed class StartSettings
{
    /// <summary>0 — компактный, 1 — обычный, 2 — большой.</summary>
    public int Size { get; set; } = 1;
    public bool ShowPinned { get; set; } = true;
    public bool ShowAll { get; set; } = true;

    public (double W, double H, int Cols, int MaxPins) Metrics => Size switch
    {
        0 => (500, 600, 5, 15),
        2 => (780, 840, 8, 32),
        _ => (620, 720, 6, 18),
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start", "settings.json");

    public static StartSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<StartSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { App.Log("настройки меню не прочитаны — беру обычные", ex); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) { App.Log("настройки меню не сохранены", ex); }
    }
}
