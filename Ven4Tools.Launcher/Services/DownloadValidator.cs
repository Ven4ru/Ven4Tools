// Services/DownloadValidator.cs
using System;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Валидация URL скачивания: разрешаем только HTTPS-ссылки на домены GitHub.
    /// Защищает от подмены ответа API — лаунчер не станет скачивать файл с чужого хоста.
    /// </summary>
    public static class DownloadValidator
    {
        public static bool IsAllowedDownloadHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != "https") return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "github.com"
                || host.EndsWith(".github.com", StringComparison.Ordinal)
                || host == "objects.githubusercontent.com"
                || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }
    }
}
