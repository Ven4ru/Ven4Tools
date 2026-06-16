using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        // Кэш загруженных иконок в памяти: ключ — URL, значение — готовый BitmapImage
        // или null, если иконку загрузить не удалось (чтобы не дёргать сеть повторно).
        private static readonly Dictionary<string, BitmapImage?> _iconCache = new();

        // Очередь иконок к загрузке: контрол Image + URL. Заполняется в LoadApps,
        // обрабатывается пачками в фоне, чтобы не грузить все 71 иконку разом.
        private readonly List<(Image Image, string Url)> _iconQueue = new();

        // Размер иконки в строке каталога (px).
        private const int IconSize = 20;

        // Таймаут загрузки одной иконки.
        private static readonly TimeSpan IconTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Создаёт пустой контейнер 20×20 под иконку и ставит её URL в очередь
        /// ленивой загрузки. Если URL пустой — возвращается просто пустое место,
        /// чтобы не ломать layout строки.
        /// </summary>
        private Image MakeAppIcon(string? iconUrl)
        {
            var image = new Image
            {
                Width = IconSize,
                Height = IconSize,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = System.Windows.Media.Stretch.Uniform,
                // Резервируем место даже без иконки, чтобы строки не «прыгали».
                SnapsToDevicePixels = true
            };

            if (!string.IsNullOrWhiteSpace(iconUrl))
                _iconQueue.Add((image, iconUrl!));

            return image;
        }

        /// <summary>
        /// Запускает фоновую загрузку всех иконок из очереди пачками по 10 штук
        /// с небольшой задержкой между пачками. Вызывать после построения списка.
        /// </summary>
        private void StartIconLoading()
        {
            if (_iconQueue.Count == 0)
                return;

            // Копируем очередь, чтобы повторный вызов LoadApps не мешал текущей загрузке.
            var queue = new List<(Image Image, string Url)>(_iconQueue);
            _iconQueue.Clear();

            _ = Task.Run(async () =>
            {
                const int batchSize = 10;
                for (int i = 0; i < queue.Count; i += batchSize)
                {
                    int end = Math.Min(i + batchSize, queue.Count);
                    var batchTasks = new List<Task>();
                    for (int j = i; j < end; j++)
                    {
                        var item = queue[j];
                        batchTasks.Add(LoadSingleIconAsync(item.Image, item.Url));
                    }

                    try { await Task.WhenAll(batchTasks); } catch { /* graceful degradation */ }

                    // Пауза между пачками — не перегружаем сеть и UI-поток.
                    await Task.Delay(150);
                }
            });
        }

        /// <summary>
        /// Загружает одну иконку с таймаутом 3 с и применяет её к Image через Dispatcher.
        /// Любая ошибка молча игнорируется — место остаётся пустым 20×20.
        /// </summary>
        private async Task LoadSingleIconAsync(Image image, string url)
        {
            try
            {
                BitmapImage? bitmap;

                // Берём из кэша, если уже загружали (или уже знаем, что не вышло).
                lock (_iconCache)
                {
                    if (_iconCache.TryGetValue(url, out bitmap))
                    {
                        if (bitmap == null) return; // ранее не загрузилось — пропускаем
                        ApplyIcon(image, bitmap);
                        return;
                    }
                }

                using var cts = new CancellationTokenSource(IconTimeout);
                byte[] data;
                try
                {
                    data = await _httpClient.GetByteArrayAsync(url, cts.Token);
                }
                catch
                {
                    // Помечаем URL как неудачный, чтобы не повторять запрос.
                    lock (_iconCache) { _iconCache[url] = null; }
                    return;
                }

                bitmap = DecodeIcon(data);
                lock (_iconCache) { _iconCache[url] = bitmap; }

                if (bitmap != null)
                    ApplyIcon(image, bitmap);
            }
            catch
            {
                // Никаких заглушек/спиннеров — просто пустое место.
            }
        }

        /// <summary>
        /// Декодирует байты иконки в замороженный BitmapImage. При любой ошибке
        /// (битый файл, неподдерживаемый формат) возвращает null.
        /// </summary>
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
                bitmap.Freeze(); // делаем потокобезопасным для передачи в UI-поток
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Применяет готовую иконку к Image в UI-потоке.
        /// </summary>
        private void ApplyIcon(Image image, BitmapImage bitmap)
        {
            try
            {
                Dispatcher.Invoke(() => image.Source = bitmap);
            }
            catch
            {
                // Окно могло закрыться — игнорируем.
            }
        }
    }
}
