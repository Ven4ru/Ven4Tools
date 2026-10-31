using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Раздел «Персонализация»: оформление Windows в стиле Венчика. Вся работа с системой —
    /// в <see cref="PersonalizationService"/>, здесь только карточки и кнопки.
    /// </summary>
    public partial class PersonalizationTab : UserControl
    {
        public sealed class PartItem
        {
            public PersonalizationPart Part { get; init; }
            public string Title { get; init; } = "";
            public string Description { get; init; } = "";
            public ImageSource? Preview { get; init; }
            public Visibility PreviewVisibility => Preview == null ? Visibility.Collapsed : Visibility.Visible;
            public bool IsAvailable { get; init; }
            public bool IsApplied { get; init; }
            public bool CanApply => IsAvailable && !IsApplied;
            // Текст карточек переводит сам TextBlock при показе — Tr() здесь не нужен.
            public string Status => !IsAvailable ? "Файлы оформления не найдены"
                : IsApplied ? "✓ Применено" : "Не применено";
            public Brush StatusBrush => (Brush)Application.Current.FindResource(
                !IsAvailable ? "StatusDanger" : IsApplied ? "StatusSuccess" : "TextSecondary");
        }

        private static readonly (PersonalizationPart Part, string Title, string Description)[] Parts =
        {
            (PersonalizationPart.Cursors, "Курсоры-лапы", "Указатель — стрелка в полоску, как шерсть Венчика, на ссылках — лапка с подушечками, а пока Windows занята, Венчик бежит на месте."),
            (PersonalizationPart.Wallpaper, "Обои", "Венчик ночью в компьютерном классе. Левая половина тёмная — значки на рабочем столе хорошо видны."),
            (PersonalizationPart.LockScreen, "Экран блокировки", "Спящий Венчик на экране входа в Windows. Пока картинка включена, сменить экран блокировки в параметрах Windows нельзя."),
            (PersonalizationPart.Sounds, "Звуки Windows", "Уведомления мяукают, ошибки и подключение устройств звучат мягкими сигналами."),
            (PersonalizationPart.Screensaver, "Заставка", "Когда компьютер простаивает, по экрану бегает Венчик. Если заставка была выключена, она включится через 10 минут простоя."),
        };

        private readonly PersonalizationService _service = PersonalizationService.Default;

        public PersonalizationTab()
        {
            InitializeComponent();
            Refresh();
        }

        private void Refresh()
        {
            var items = Parts.Select(p => new PartItem
            {
                Part = p.Part,
                Title = p.Title,
                Description = p.Description,
                Preview = LoadPreview(_service.PreviewPath(p.Part)),
                IsAvailable = _service.IsAvailable(p.Part),
                IsApplied = _service.IsApplied(p.Part),
            }).ToList();
            lstParts.ItemsSource = items;
            btnApplyAll.IsEnabled = items.Any(i => i.CanApply);
            btnRevertAll.IsEnabled = items.Any(i => i.IsApplied);
        }

        private static ImageSource? LoadPreview(string? path)
        {
            if (path == null) return null;
            try
            {
                // Без OnLoad файл остаётся открытым, и следующее «Применить» не сможет его скопировать.
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 640;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                AppLogger.Write("⚠️ Персонализация: не открыта картинка — " + ex.Message);
                return null;
            }
        }

        private async Task RunAsync(IEnumerable<PersonalizationPart> parts, bool apply)
        {
            IsEnabled = false;
            var failed = new List<string>();
            try
            {
                foreach (var part in parts)
                {
                    try
                    {
                        await Task.Run(() => { if (apply) _service.Apply(part); else _service.Revert(part); });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Write($"❌ Персонализация: «{part}» — {ex.Message}");
                        failed.Add(Tr(Parts.First(p => p.Part == part).Title));
                    }
                }
            }
            finally
            {
                IsEnabled = true;
                Refresh();
            }
            if (failed.Count > 0)
                MessageBox.Show(string.Format("Не получилось: {0}. Подробности — в журнале.", string.Join(", ", failed)),
                    "Персонализация", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is PartItem item) await RunAsync(new[] { item.Part }, apply: true);
        }

        private async void Revert_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is PartItem item) await RunAsync(new[] { item.Part }, apply: false);
        }

        private async void ApplyAll_Click(object sender, RoutedEventArgs e) =>
            await RunAsync(Parts.Select(p => p.Part).Where(p => _service.IsAvailable(p) && !_service.IsApplied(p)), apply: true);

        private async void RevertAll_Click(object sender, RoutedEventArgs e) =>
            await RunAsync(Parts.Select(p => p.Part).Where(_service.IsApplied), apply: false);
    }
}
