using System.Collections.Generic;
using System.IO;

namespace Ven4Tools.Launcher.Services;

internal enum SetupComponent
{
    Winget,
    Chocolatey
}

internal static class SetupComponentRequestService
{
    private static readonly (SetupComponent Component, string MarkerName)[] Markers =
    [
        (SetupComponent.Winget, "install-winget.pending"),
        (SetupComponent.Chocolatey, "install-chocolatey.pending")
    ];

    /// <summary>
    /// Есть ли непрочитанные запросы. Ничего не удаляет: нужно, чтобы не занимать слот
    /// долгих операций (и не писать в журнал об отказе) там, где ставить нечего.
    /// Решение о фактической установке принимает <see cref="Consume"/> — только он
    /// гарантирует одноразовость запроса.
    /// </summary>
    public static bool HasPending(string launcherDirectory)
    {
        if (string.IsNullOrWhiteSpace(launcherDirectory))
            return false;

        foreach (var marker in Markers)
        {
            if (File.Exists(Path.Combine(launcherDirectory, marker.MarkerName)))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<SetupComponent> Consume(string launcherDirectory)
    {
        var requested = new List<SetupComponent>();
        if (string.IsNullOrWhiteSpace(launcherDirectory))
            return requested;

        foreach (var marker in Markers)
        {
            string markerPath = Path.Combine(launcherDirectory, marker.MarkerName);
            if (!File.Exists(markerPath))
                continue;

            try
            {
                File.Delete(markerPath);
                requested.Add(marker.Component);
            }
            catch
            {
                // Не запускаем действие, если не удалось гарантировать
                // одноразовость запроса.
            }
        }

        return requested;
    }
}
