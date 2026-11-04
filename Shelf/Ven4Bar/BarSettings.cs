using System;
using System.IO;
using System.Text.Json;

namespace Ven4Bar;

/// <summary>
/// Настройки своей панели (%LOCALAPPDATA%\Ven4Bar\settings.json). Меняются из правого щелчка по панели,
/// после изменения панель перезапускается. Закрывают главные жалобы на панель Windows 11: нельзя к другому краю,
/// нельзя поменять размер, нет автоскрытия у бокового края, окна без подписей.
/// </summary>
internal sealed class BarSettings
{
    public const int Left = 0, Top = 1, Right = 2, Bottom = 3;

    /// <summary>Край: 0 слева, 1 сверху, 2 справа, 3 снизу.</summary>
    public int Edge { get; set; } = Bottom;
    /// <summary>0 — маленькая, 1 — обычная, 2 — большая.</summary>
    public int Size { get; set; } = 1;
    /// <summary>solid — плотная, glass — стекло, clear — прозрачная, island — «остров» посередине.</summary>
    public string Style { get; set; } = "solid";
    public bool AutoHide { get; set; }
    public bool Labels { get; set; }

    public bool Vertical => Edge is Left or Right;
    public double Thick => Size switch { 0 => 40, 2 => 58, _ => 48 };
    public double Icon => Size switch { 0 => 20, 2 => 30, _ => 24 };
    public double Button => Size switch { 0 => 36, 2 => 52, _ => 44 };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Bar", "settings.json");

    public static BarSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<BarSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
