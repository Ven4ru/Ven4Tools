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
    internal static bool IsAllowed(string? url) => TryGetAllowed(url, out _);

    private static bool TryGetAllowed(string? url, out Uri? allowed)
    {
        allowed = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        foreach (var host in AllowedHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                allowed = uri;
                return true;
            }
        }
        return false;
    }

    /// <summary>Открывает ссылку в браузере; недопустимую — не открывает и пишет в журнал.</summary>
    public static void Open(string? url)
    {
        if (!TryGetAllowed(url, out var uri) || uri == null)
        {
            LauncherLog.Write($"[Ссылки] Отклонена недопустимая ссылка: {url}");
            return;
        }
        try
        {
            // В оболочку уходит разобранный и экранированный адрес, а не исходная строка:
            // Uri.TryCreate прощает пробелы по краям и неэкранированные символы, и
            // проверенным оказывался не ровно тот текст, что получал ShellExecute.
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LauncherLog.Write($"[Ссылки] Не удалось открыть браузер: {ex.Message}");
        }
    }
}
