using System;

namespace Ven4Tools.Services
{
    // Официального сайта в каталоге отдельным полем нет — извлекаем домен из
    // downloadUrl как разумное приближение (см. спеку прототипа карточки).
    public static class HomepageUrlHelper
    {
        public static string? ExtractHomepage(string? downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return null;
            // Каталог подписан ECDSA, но карточка передаёт результат прямо в
            // Process.Start(UseShellExecute=true) по клику — на всякий случай не
            // доверяем схемам вроде file:/javascript:, только обычные веб-ссылки.
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            return $"{uri.Scheme}://{uri.Host}";
        }
    }
}
