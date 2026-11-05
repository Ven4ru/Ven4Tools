using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ven4Start;

/// <summary>Закреплённые программы (идентификаторы shell:AppsFolder) в %LOCALAPPDATA%\Ven4Start\pinned.json.</summary>
internal static class Pins
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start", "pinned.json");

    // Если файла нет — закрепить то, что есть почти у всех (по названию, русскому или английскому).
    private static readonly string[][] Defaults =
    {
        new[] { "Проводник", "File Explorer" }, new[] { "Microsoft Edge" }, new[] { "Параметры", "Settings" },
        new[] { "Ven4Tools" }, new[] { "Microsoft Store" }, new[] { "Блокнот", "Notepad" },
        new[] { "Калькулятор", "Calculator" }, new[] { "Фотографии", "Photos" }, new[] { "Paint" },
        new[] { "Терминал", "Terminal", "Windows PowerShell" }, new[] { "Почта", "Outlook", "Mail" }, new[] { "Ножницы", "Snipping Tool" },
    };

    public static List<string> Load(IReadOnlyList<AppEntry> apps)
    {
        try
        {
            if (File.Exists(FilePath))
                // null в испорченном файле ронял меню (ContainsKey(null)); повторы — двойные плитки.
                return (JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(FilePath)) ?? new())
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) { App.Log("закреплённые не прочитаны — беру набор по умолчанию", ex); }
        var list = new List<string>();
        foreach (var names in Defaults)
        {
            var hit = apps.FirstOrDefault(a => names.Any(n => string.Equals(a.Name, n, StringComparison.CurrentCultureIgnoreCase)))
                   ?? apps.FirstOrDefault(a => names.Any(n => a.Name.StartsWith(n, StringComparison.CurrentCultureIgnoreCase)));
            if (hit != null && !list.Contains(hit.ParsingName)) list.Add(hit.ParsingName);
        }
        return list;
    }

    public static void Save(List<string> pins)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(pins));
        }
        catch (Exception ex) { App.Log("закреплённые не сохранены", ex); }
    }
}
