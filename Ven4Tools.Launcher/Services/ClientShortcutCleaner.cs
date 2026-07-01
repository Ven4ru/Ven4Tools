using System;
using System.Collections.Generic;
using System.IO;

namespace Ven4Tools.Launcher.Services;

internal static class ClientShortcutCleaner
{
    internal static readonly string[] ClientShortcutNames =
    {
        "Ven4Tools.lnk",
        "Ven4Tools Client.lnk"
    };

    internal static void Clean(
        IEnumerable<string> desktopDirectories,
        IEnumerable<string> startMenuProgramDirectories)
    {
        foreach (string desktop in desktopDirectories)
        {
            DeleteClientShortcuts(desktop);
        }

        foreach (string programsDirectory in startMenuProgramDirectories)
        {
            DeleteClientShortcuts(programsDirectory);

            string ven4ToolsDirectory = Path.Combine(programsDirectory, "Ven4Tools");
            DeleteClientShortcuts(ven4ToolsDirectory);
            DeleteDirectoryIfEmpty(ven4ToolsDirectory);
        }
    }

    private static void DeleteClientShortcuts(string directory)
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
