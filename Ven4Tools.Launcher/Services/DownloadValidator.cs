// Services/DownloadValidator.cs
using System;
using System.Net.Http;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Валидация URL скачивания: разрешаем только HTTPS-ссылки на доверенные домены
    /// (GitHub и Microsoft). Защищает от подмены ответа API — лаунчер не станет
    /// скачивать файл с чужого хоста.
    /// </summary>
    public static class DownloadValidator
    {
        public static bool IsAllowedDownloadHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return IsAllowedUri(uri);
        }

        /// <summary>
        /// Проверка итогового URL после всех редиректов: HttpClient следует за ними
        /// автоматически, поэтому хост из исходной ссылки может отличаться от того,
        /// откуда фактически пришли данные.
        /// </summary>
        public static bool IsAllowedDownloadHostAfterRedirect(HttpResponseMessage response)
        {
            var uri = response.RequestMessage?.RequestUri;
            if (uri == null) return false;
            return IsAllowedUri(uri);
        }

        private static bool IsAllowedUri(Uri uri)
        {
            if (uri.Scheme != "https") return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "github.com"
                || host.EndsWith(".github.com", StringComparison.Ordinal)
                || host == "objects.githubusercontent.com"
                || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)
                || host == "cdn.ven4tools.ru"
                || host == "aka.ms"
                || host == "go.microsoft.com"
                || host == "download.microsoft.com"
                || host == "microsoft.com"
                || host.EndsWith(".microsoft.com", StringComparison.Ordinal);
        }
    }
}
