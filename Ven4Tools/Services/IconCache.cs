using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    // Вынесено из CatalogTab.Icons.cs при переходе каталога на MVVM: раньше кэш и
    // очередь загрузки жили в code-behind вкладки, теперь строки каталога сами
    // запрашивают иконку у общего кэша по URL.
    public static class IconCache
    {
        private static readonly Dictionary<string, BitmapImage?> _cache = new();
        // Единый с остальными сервисами стиль: static readonly + инициализатор.
        // Timeout бесконечный — фактический предел задаётся per-request через
        // CancellationTokenSource(IconTimeout), как и раньше при factory-варианте.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "Ven4Tools" } }
        };
        private const int IconSize = 20;
        private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(3);

        // Дисковый кеш переживает перезапуск приложения — без него каждый старт клиента
        // заново тянул ~72 иконки со сторонних CDN одновременно (без ограничителя
        // параллелизма), что и лишний трафик/задержка, и повторяющееся раскрытие IP
        // третьим сторонам на каждом запуске. Ключ — sha256 URL, а не сам URL: имя файла
        // не должно зависеть от произвольных символов в адресе стороннего хоста.
        private static readonly string _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "icons");

        public static async Task<BitmapImage?> GetIconAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            // Параноидальный режим: иконки каталога грузятся со сторонних CDN-хостов
            // (не только с доверенного источника каталога) — раскрывают IP third-party
            // серверам, чего пользователь в этом режиме не ожидает. Офлайн-режим:
            // сетевой запрос бессмысленен, каталог и так берётся из кэша.
            bool skipNetwork = ProfileService.Current.ParanoidMode || ProfileService.Current.OfflineMode;

            lock (_cache)
            {
                if (_cache.TryGetValue(url, out var cached)) return cached;
            }

            string diskPath = Path.Combine(_diskCacheDir, UrlToFileName(url));
            if (File.Exists(diskPath))
            {
                try
                {
                    var diskBitmap = DecodeIcon(await File.ReadAllBytesAsync(diskPath));
                    if (diskBitmap != null)
                    {
                        lock (_cache) { _cache[url] = diskBitmap; }
                        return diskBitmap;
                    }
                }
                catch { /* повреждённый файл кеша — просто перекачаем при разрешённой сети */ }
            }

            if (skipNetwork) return null;

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
            if (bitmap != null)
            {
                try { await FileHelper.WriteAllBytesAtomicAsync(diskPath, data); }
                catch { /* дисковый кеш — best-effort, иконка в памяти уже есть */ }
            }
            return bitmap;
        }

        private static string UrlToFileName(string url)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
            return Convert.ToHexString(hash).ToLowerInvariant() + ".bin";
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
