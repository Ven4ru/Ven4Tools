// Helpers/PersonalDataSanitizer.cs
using System;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Очистка текста от персональных данных перед тем, как он покинет машину
/// пользователя или ляжет в файл, который пользователь прикладывает к обращению.
///
/// Раньше жила внутри <c>GitHubService</c>, хотя к GitHub API отношения не имеет:
/// вызывается из окна краш-отчёта, окна отчёта об установке и из глобального
/// обработчика необработанных исключений (App.xaml.cs), который никакой сети не
/// касается вообще. Вынесена в отдельную утилиту, чтобы не тащить за собой
/// HTTP-клиент и кэш релизов ради одной строковой замены.
/// </summary>
public static class PersonalDataSanitizer
{
    /// <summary>
    /// Удаление персональных данных из текста перед отправкой в публичный репозиторий:
    /// имя пользователя, имя машины и пути вида C:\Users\имя\ заменяются плейсхолдерами.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // Пути профилей: C:\Users\имя\ → C:\Users\<пользователь>\
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"([A-Za-z]:\\Users\\)[^\\\r\n]+",
            "$1<пользователь>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Тот же путь с forward-slash и UNC-путь без буквы диска — тот же класс,
        // что и в клиентском CrashReportService.SanitizePath (кросс-модульная
        // согласованность).
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"([A-Za-z]:/Users/)[^/\r\n]+",
            "$1<пользователь>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(\\\\[^\\\r\n]+\\Users\\)[^\\\r\n]+",
            "$1<пользователь>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Имя пользователя и имя машины в произвольных местах текста.
        // Короткие значения (< 3 символов) не заменяем — слишком много ложных срабатываний.
        string user = Environment.UserName;
        if (!string.IsNullOrEmpty(user) && user.Length >= 3)
            text = text.Replace(user, "<пользователь>", StringComparison.OrdinalIgnoreCase);

        string machine = Environment.MachineName;
        if (!string.IsNullOrEmpty(machine) && machine.Length >= 3)
            text = text.Replace(machine, "<машина>", StringComparison.OrdinalIgnoreCase);

        return text;
    }

    /// <summary>
    /// Короткий хэш идентификатора сессии: достаточен для дедупликации отчётов,
    /// но не раскрывает исходный SessionId в публичном репозитории.
    /// </summary>
    public static string HashSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return "";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sessionId));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}
