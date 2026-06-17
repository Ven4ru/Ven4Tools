using System;
using System.Net.Http;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Мягкая проверка URL для загрузки установщиков в клиенте.
    /// В отличие от строгого allowlist лаунчера, здесь обязателен только HTTPS —
    /// каталог содержит множество vendor-хостов.
    /// </summary>
    public static class DownloadValidator
    {
        public static bool ValidateUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps;
        }

        public static bool ValidateAfterRedirect(HttpResponseMessage response)
        {
            var uri = response.RequestMessage?.RequestUri;
            if (uri == null) return false;
            return uri.Scheme == Uri.UriSchemeHttps;
        }
    }
}
