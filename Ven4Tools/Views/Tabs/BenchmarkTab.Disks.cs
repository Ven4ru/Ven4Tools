using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.Views.Tabs
{
    public partial class BenchmarkTab : UserControl
    {
        /// <summary>Заполняет список накопителей и выбирает первый пригодный для теста.</summary>
        private async Task LoadDisksAsync()
        {
            txtDiskHint.Text = "Определение накопителей...";
            cmbDisks.Items.Clear();

            try
            {
                _disks = await DiskInventoryService.GetDisksAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkTab.LoadDisksAsync");
                _disks.Clear();
            }

            foreach (var disk in _disks)
            {
                string capacity = disk.SizeBytes > 0
                    ? " — " + BenchmarkReportBuilder.FormatCapacity(disk.SizeBytes)
                    : "";
                string suffix = disk.CanBenchmark ? "" : " (нет тома для теста)";

                cmbDisks.Items.Add(new ComboBoxItem
                {
                    Content = $"Диск {disk.Index}: {disk.FriendlyName}{capacity}{suffix}",
                    Tag = disk,
                    IsEnabled = disk.CanBenchmark
                });
            }

            if (cmbDisks.Items.Count == 0)
            {
                txtDiskHint.Text = "Накопители не обнаружены. Подробности — в журнале приложения.";
                btnRunBenchmark.IsEnabled = false;
                return;
            }

            // Выбираем первый накопитель, на котором есть куда положить тестовый файл.
            for (int index = 0; index < _disks.Count; index++)
            {
                if (_disks[index].CanBenchmark)
                {
                    cmbDisks.SelectedIndex = index;
                    break;
                }
            }

            if (cmbDisks.SelectedIndex < 0)
            {
                txtDiskHint.Text = "Ни на одном накопителе нет тома, пригодного для теста.";
                btnRunBenchmark.IsEnabled = false;
            }
        }

        private async void CmbDisks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedDisk = (cmbDisks.SelectedItem as ComboBoxItem)?.Tag as PhysicalDiskInfo;
            ShowDiskDetails(_selectedDisk);
            FillVolumes(_selectedDisk);
            await RefreshWarningsAsync();
        }

        private async void CmbVolumes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedVolume = (cmbVolumes.SelectedItem as ComboBoxItem)?.Tag as BenchmarkVolumeInfo;
            await RefreshWarningsAsync();
        }

        private async void CmbFileSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await RefreshWarningsAsync();
        }

        private void ShowDiskDetails(PhysicalDiskInfo? disk)
        {
            if (disk == null)
            {
                txtModel.Text = "—";
                txtCapacity.Text = "—";
                txtMedia.Text = "—";
                txtConnection.Text = "—";
                txtCeiling.Text = "—";
                return;
            }

            txtModel.Text = disk.FriendlyName;
            txtCapacity.Text = disk.SizeBytes > 0
                ? BenchmarkReportBuilder.FormatCapacity(disk.SizeBytes)
                : "неизвестно";

            txtMedia.Text = BenchmarkReportBuilder.DescribeMediaWithSpindle(disk);

            txtConnection.Text = BenchmarkReportBuilder.DescribeConnection(disk);

            // Потолок показываем только когда параметры линии получены достоверно.
            if (disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
            {
                txtCeiling.Text = BenchmarkReportBuilder.FormatSpeedRounded(
                    disk.Link.CeilingMegabytesPerSecond) + " МБ/с";
                txtCeiling.Foreground = (Brush)FindResource("TextPrimary");
            }
            else
            {
                txtCeiling.Text = "неизвестно — параметры интерфейса недоступны";
                txtCeiling.Foreground = (Brush)FindResource("TextSecondary");
            }
        }

        private void FillVolumes(PhysicalDiskInfo? disk)
        {
            cmbVolumes.Items.Clear();
            _selectedVolume = null;

            if (disk == null)
            {
                txtDiskHint.Text = "Накопитель не выбран.";
                return;
            }

            foreach (var volume in disk.Volumes)
            {
                if (!volume.IsReady) continue;

                string label = string.IsNullOrWhiteSpace(volume.Label) ? "" : $" «{volume.Label}»";
                string system = volume.IsSystem ? ", системный" : "";
                cmbVolumes.Items.Add(new ComboBoxItem
                {
                    Content = $"{volume.Letter}{label} — свободно " +
                              BenchmarkReportBuilder.FormatCapacity(volume.FreeBytes) + system,
                    Tag = volume
                });
            }

            if (cmbVolumes.Items.Count == 0)
            {
                txtDiskHint.Text = "На этом накопителе нет тома, пригодного для теста. " +
                                   "Тест выполняется через файл, поэтому нужен размеченный том с файловой системой.";
                btnRunBenchmark.IsEnabled = false;
                return;
            }

            // Несистемный том предпочтительнее: на нём меньше постороннего фона.
            int preferred = 0;
            for (int index = 0; index < cmbVolumes.Items.Count; index++)
            {
                if ((cmbVolumes.Items[index] as ComboBoxItem)?.Tag is BenchmarkVolumeInfo volume &&
                    !volume.IsSystem)
                {
                    preferred = index;
                    break;
                }
            }
            cmbVolumes.SelectedIndex = preferred;

            txtDiskHint.Text = "Тест измеряет скорость выбранного накопителя через временный файл на указанном томе.";
        }

        /// <summary>
        /// Пересобирает список предупреждений и решает, можно ли запускать тест.
        ///
        /// Выбор накопителя перевыставляет и том, поэтому метод легко вызывается дважды
        /// внахлёст. Токен гарантирует, что панель заполнит только последний вызов: без него
        /// два параллельных прохода дописывали в неё одни и те же предупреждения дважды.
        /// </summary>
        private async Task RefreshWarningsAsync()
        {
            int token = ++_warningsToken;

            if (_selectedVolume == null)
            {
                pnlWarningItems.Children.Clear();
                _warnings.Clear();
                pnlWarnings.Visibility = Visibility.Collapsed;
                btnRunBenchmark.IsEnabled = false;
                return;
            }

            var volume = _selectedVolume;
            bool allowed = BenchmarkWarningService.TryValidateFreeSpace(
                volume, SelectedFileSize, out string blockingError);

            // Опрос состояния шифрования тома занимает у Windows несколько секунд, поэтому
            // объясняем пользователю, почему кнопка запуска пока недоступна.
            if (!_running) txtRunStatus.Text = "Проверяем том...";

            var collected = new List<string>();
            try
            {
                collected = await BenchmarkWarningService.CollectAsync(volume);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkTab.RefreshWarningsAsync");
            }

            // Пока шёл опрос тома, пользователь мог переключить накопитель — тогда этот
            // результат уже неактуален и трогать интерфейс не должен.
            if (token != _warningsToken) return;

            if (!allowed) collected.Insert(0, blockingError);
            _warnings = collected;

            pnlWarningItems.Children.Clear();
            foreach (string warning in _warnings)
            {
                pnlWarningItems.Children.Add(new TextBlock
                {
                    Text = "• " + warning,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                    Foreground = (Brush)FindResource("TextPrimary")
                });
            }

            pnlWarnings.Visibility = _warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            btnRunBenchmark.IsEnabled = allowed && !_running;

            if (!_running)
            {
                txtRunStatus.Text = allowed
                    ? "Тест ещё не запускался"
                    : "Запуск невозможен — смотрите предупреждение выше";
            }
        }
    }
}
