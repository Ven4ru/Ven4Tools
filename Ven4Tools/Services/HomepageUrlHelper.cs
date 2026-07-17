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
            return $"{uri.Scheme}://{uri.Host}";
        }
    }
}
