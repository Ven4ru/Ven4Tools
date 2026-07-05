using System;
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
