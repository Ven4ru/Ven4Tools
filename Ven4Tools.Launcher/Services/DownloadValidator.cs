// Services/DownloadValidator.cs
using System;
using System.Linq;
using System.Net.Http;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Валидация параметров скачивания: разрешаем только HTTPS-ссылки на доверенные
    /// домены (GitHub и Microsoft) и требуем полноценный SHA256-хеш там, где он
    /// обязателен. Защищает от подмены ответа API — лаунчер не станет скачивать
    /// файл с чужого хоста и не примет файл без подтверждённой контрольной суммы.
    /// </summary>
    public static class DownloadValidator
    {
        /// <summary>
        /// Полный SHA256-дайджест в hex: ровно 64 шестнадцатеричных символа.
        /// Обрезанный или пустой хеш проверкой целостности не является — вызывающий
        /// код обязан отказаться от загрузки (fail-closed).
        /// </summary>
        public static bool IsValidSha256(string? value)
        {
            return value?.Length == 64 && value.All(Uri.IsHexDigit);
        }

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

            // Зеркало релизов на хостинге ven4tools.ru: доверяем ТОЛЬКО пути /releases/.
            // Остальной сайт (в т.ч. /api/db.php и любые другие эндпоинты) НЕ является
            // доверенным источником загрузки — на этом хосте живёт обычный сайт с API,
            // которому нельзя доверять так же широко, как выделенному CDN.
            if (host == "ven4tools.ru" || host == "www.ven4tools.ru")
                return uri.AbsolutePath.StartsWith("/releases/", StringComparison.OrdinalIgnoreCase);

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
