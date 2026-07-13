using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Ven4Tools.Services
{
    // Вынесено из CatalogTab.Icons.cs при переходе каталога на MVVM: раньше кэш и
    // очередь загрузки жили в code-behind вкладки, теперь строки каталога сами
    // запрашивают иконку у общего кэша по URL.
    public static class IconCache
    {
        private static readonly Dictionary<string, BitmapImage?> _cache = new();
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private const int IconSize = 20;
        private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(3);

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            return client;
        }

        public static async Task<BitmapImage?> GetIconAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            lock (_cache)
            {
                if (_cache.TryGetValue(url, out var cached)) return cached;
            }

            if (!DownloadValidator.ValidateUrl(url))
            {
                lock (_cache) { _cache[url] = null; }
                return null;
            }

            byte[] data;
            try
            {
                using var cts = new CancellationTokenSource(IconTimeout);
                data = await _httpClient.GetByteArrayAsync(url, cts.Token);
            }
            catch
            {
                lock (_cache) { _cache[url] = null; }
                return null;
            }

            var bitmap = DecodeIcon(data);
            lock (_cache) { _cache[url] = bitmap; }
            return bitmap;
        }

        private static BitmapImage? DecodeIcon(byte[] data)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.None;
                bitmap.DecodePixelWidth = IconSize;
                bitmap.StreamSource = new MemoryStream(data);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }
    }
}
