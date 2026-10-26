using System;
using System.Diagnostics;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Открытие внешних ссылок из окон «Подлинность», «Антивирус», «Диагностика соединения».
/// Ссылка уходит в Process.Start(UseShellExecute=true), поэтому открываем только https и
/// только сайты из короткого списка: лаборатории антивирусов, VirusTotal, Microsoft и
/// страницы релизов нашего репозитория. Ни file:, ни произвольный хост сюда не пройдут.
/// </summary>
internal static class SafeLinkOpener
{
    private static readonly string[] AllowedHosts =
    {
        "opentip.kaspersky.com",
        "vms.drweb.ru",
        "www.microsoft.com",
        "www.virustotal.com",
        "github.com",
    };

    /// <summary>Можно ли открыть ссылку. Чистая функция — покрыта unit-тестами.</summary>
    internal static bool IsAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        foreach (var host in AllowedHosts)
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Открывает ссылку в браузере; недопустимую — не открывает и пишет в журнал.</summary>
    public static void Open(string? url)
    {
        if (!IsAllowed(url))
        {
            LauncherLog.Write($"[Ссылки] Отклонена недопустимая ссылка: {url}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"[Ссылки] Не удалось открыть браузер: {ex.Message}");
        }
    }
}
