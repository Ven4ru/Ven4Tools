using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Ярлыки клиента при «Удалить клиент». Удаляется только ярлык, цель которого лежит
/// внутри папки клиента: «Ven4Tools.lnk» на рабочем столе может вести и на другую
/// программу с тем же именем или на вторую копию клиента — такой остаётся. Цель не
/// читается (битый ярлык, нет оболочки) — тоже остаётся: не удалось убедиться, что
/// ярлык наш.
/// </summary>
internal static class ClientShortcutCleaner
{
    internal static readonly string[] ClientShortcutNames =
    {
        "Ven4Tools.lnk",
        "Ven4Tools Client.lnk"
    };

    /// <summary>
    /// Удаляет ярлыки клиента, ведущие внутрь <paramref name="clientPath"/>. Возвращает
    /// ярлыки, оставленные из-за чужой или нечитаемой цели (для журнала).
    /// <paramref name="readTarget"/> — чтение цели .lnk (по умолчанию через WScript.Shell;
    /// тесты подменяют).
    /// </summary>
    internal static IReadOnlyList<string> Clean(
        IEnumerable<string> desktopDirectories,
        IEnumerable<string> startMenuProgramDirectories,
        string clientPath,
        Func<string, string?>? readTarget = null)
    {
        readTarget ??= ReadShortcutTarget;
        var kept = new List<string>();

        foreach (string desktop in desktopDirectories)
        {
            DeleteClientShortcuts(desktop, clientPath, readTarget, kept);
        }

        foreach (string programsDirectory in startMenuProgramDirectories)
        {
            DeleteClientShortcuts(programsDirectory, clientPath, readTarget, kept);

            string ven4ToolsDirectory = Path.Combine(programsDirectory, "Ven4Tools");
            DeleteClientShortcuts(ven4ToolsDirectory, clientPath, readTarget, kept);
            DeleteDirectoryIfEmpty(ven4ToolsDirectory);
        }

        return kept;
    }

    /// <summary>Цель ярлыка внутри папки клиента (или сама папка). Покрыто unit-тестами.</summary>
    internal static bool PointsIntoClientFolder(string? target, string clientPath) =>
        !string.IsNullOrWhiteSpace(clientPath) &&
        ClientShortcutRetargeter.MapPath(target, clientPath, clientPath) != null;

    private static void DeleteClientShortcuts(
        string directory, string clientPath, Func<string, string?> readTarget, List<string> kept)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        foreach (string name in ClientShortcutNames)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                continue;
            }

            string? target;
            try
            {
                target = readTarget(path);
            }
            catch
            {
                target = null;
            }

            if (!PointsIntoClientFolder(target, clientPath))
            {
                kept.Add(path);
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ошибка удаления отдельного ярлыка не должна прерывать очистку клиента.
            }
        }
    }

    // Та же оболочка, что у ClientShortcutRetargeter: .lnk читается через WScript.Shell.
    private static string? ReadShortcutTarget(string lnk)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
        if (shellType == null) return null;

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            dynamic wsh = shell;
            shortcut = wsh.CreateShortcut(lnk);
            dynamic s = shortcut!;
            return (string?)s.TargetPath;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
            {
                try { Marshal.ReleaseComObject(shortcut); } catch { }
            }
            if (shell != null && Marshal.IsComObject(shell))
            {
                try { Marshal.ReleaseComObject(shell); } catch { }
            }
        }
    }

    private static void DeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) &&
                Directory.GetFileSystemEntries(directory).Length == 0)
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Папка меню «Пуск» может быть занята оболочкой; это не мешает удалению клиента.
        }
    }
}
