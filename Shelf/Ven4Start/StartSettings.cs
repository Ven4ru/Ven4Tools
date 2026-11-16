using System;
using System.IO;
using System.Runtime.Serialization;

namespace Ven4Start;

/// <summary>
/// Настройки меню (%LOCALAPPDATA%\Ven4Start\settings.json): размер и какие разделы показывать —
/// главные жалобы на родной Пуск Windows 11: «огромный, размер не меняется, лишние разделы».
/// </summary>
[DataContract]
internal sealed class StartSettings
{
    /// <summary>0 — компактный, 1 — обычный, 2 — большой.</summary>
    [DataMember(Name = "Size")] public int Size { get; set; } = 1;
    [DataMember(Name = "ShowPinned")] public bool ShowPinned { get; set; } = true;
    [DataMember(Name = "ShowAll")] public bool ShowAll { get; set; } = true;

    // DataContractJsonSerializer не вызывает конструктор: без этого пропущенные в файле поля стали бы false/0.
    [OnDeserializing]
    private void Defaults(StreamingContext _) { Size = 1; ShowPinned = true; ShowAll = true; }

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
            if (File.Exists(FilePath)) return JsonFile.Read<StartSettings>(FilePath) ?? new();
        }
        catch (Exception ex) { App.Log("настройки меню не прочитаны — беру обычные", ex); }
        return new();
    }

    public void Save()
    {
        try { JsonFile.Write(FilePath, this); }
        catch (Exception ex) { App.Log("настройки меню не сохранены", ex); }
    }
}
