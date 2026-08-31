using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.ViewModels
{
    public sealed partial class BenchmarkViewModel
    {
        private void FillFileSizeOptions()
        {
            var options = BenchmarkPresets.FileSizes
                .Select(size => new FileSizeOptionItem { Label = BenchmarkReportBuilder.FormatBinarySize(size), Bytes = size })
                .ToList();
            FileSizeOptions = options;
            SelectedFileSizeOption = options[0];
        }

        /// <summary>Заполняет список накопителей и выбирает первый пригодный для теста.</summary>
        private async Task LoadDisksAsync()
        {
            DiskHintText = "Определение накопителей...";
            DiskOptions = Array.Empty<DiskOptionItem>();

            try
            {
                _disks = await DiskInventoryService.GetDisksAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.LoadDisksAsync");
                _disks.Clear();
            }

            var options = new List<DiskOptionItem>();
            foreach (var disk in _disks)
            {
                string capacity = disk.SizeBytes > 0
                    ? " — " + BenchmarkReportBuilder.FormatCapacity(disk.SizeBytes)
                    : "";
                string suffix = disk.CanBenchmark ? "" : " (нет тома для теста)";

                options.Add(new DiskOptionItem
                {
                    Label = $"Диск {disk.Index}: {disk.FriendlyName}{capacity}{suffix}",
                    Disk = disk,
                    CanBenchmark = disk.CanBenchmark
                });
            }
            DiskOptions = options;

            if (options.Count == 0)
            {
                DiskHintText = "Накопители не обнаружены. Подробности — в журнале приложения.";
                IsRunEnabled = false;
                return;
            }

            // Выбираем первый накопитель, на котором есть куда положить тестовый файл.
            int selectIndex = -1;
            for (int index = 0; index < _disks.Count; index++)
            {
                if (_disks[index].CanBenchmark) { selectIndex = index; break; }
            }

            if (selectIndex >= 0)
            {
                SelectedDiskOption = options[selectIndex];
            }
            else
            {
                DiskHintText = "Ни на одном накопителе нет тома, пригодного для теста.";
                IsRunEnabled = false;
            }
        }

        internal void ShowDiskDetails(PhysicalDiskInfo? disk)
        {
            if (disk == null)
            {
                ModelText = "—";
                CapacityText = "—";
                MediaText = "—";
                ConnectionText = "—";
                CeilingText = "—";
                return;
            }

            ModelText = disk.FriendlyName;
            CapacityText = disk.SizeBytes > 0
                ? BenchmarkReportBuilder.FormatCapacity(disk.SizeBytes)
                : "неизвестно";

            MediaText = BenchmarkReportBuilder.DescribeMediaWithSpindle(disk);
            ConnectionText = BenchmarkReportBuilder.DescribeConnection(disk);

            // Потолок показываем только когда параметры линии получены достоверно.
            if (disk.Link.IsKnown && disk.Link.CeilingMegabytesPerSecond > 0)
            {
                CeilingText = BenchmarkReportBuilder.FormatSpeedRounded(disk.Link.CeilingMegabytesPerSecond) + " МБ/с";
                CeilingBrush = ResolveBrush("TextPrimary");
            }
            else
            {
                CeilingText = "неизвестно — параметры интерфейса недоступны";
                CeilingBrush = ResolveBrush("TextSecondary");
            }
        }

        /// <summary>
        /// Пересобирает список томов текущего накопителя и, если есть подходящие,
        /// автоматически выбирает предпочтительный ЧЕРЕЗ ПУБЛИЧНЫЙ СЕТТЕР
        /// SelectedVolumeOption — ровно как оригинал переставлял cmbVolumes.SelectedIndex,
        /// что запускало реальное событие выбора и вложенный вызов RefreshWarningsAsync.
        /// Сброс в начале ТОЖЕ обязан поднять RefreshWarningsAsync — ровно как
        /// оригинальный cmbVolumes.Items.Clear() поднимал SelectionChanged ещё ДО
        /// переустановки SelectedIndex. Без этого вызова между переключением дисков
        /// и повторным опросом WMI/BitLocker кнопка запуска и текст предупреждений
        /// на несколько секунд остаются от ПРЕДЫДУЩЕГО диска, хотя _selectedVolume
        /// уже указывает на новый — реальное расхождение с оригиналом, не косметика.
        /// </summary>
        private void FillVolumeOptions(PhysicalDiskInfo? disk)
        {
            SetField(ref _selectedVolumeOption, null, nameof(SelectedVolumeOption));
            _selectedVolume = null;
            _ = RefreshWarningsAsync();

            var options = new List<VolumeOptionItem>();

            if (disk == null)
            {
                DiskHintText = "Накопитель не выбран.";
                VolumeOptions = options;
                return;
            }

            foreach (var volume in disk.Volumes)
            {
                if (!volume.IsReady) continue;

                string label = string.IsNullOrWhiteSpace(volume.Label) ? "" : $" «{volume.Label}»";
                string system = volume.IsSystem ? ", системный" : "";
                options.Add(new VolumeOptionItem
                {
                    Label = $"{volume.Letter}{label} — свободно " +
                            BenchmarkReportBuilder.FormatCapacity(volume.FreeBytes) + system,
                    Volume = volume
                });
            }

            VolumeOptions = options;

            if (options.Count == 0)
            {
                DiskHintText = "На этом накопителе нет тома, пригодного для теста. " +
                                "Тест выполняется через файл, поэтому нужен размеченный том с файловой системой.";
                IsRunEnabled = false;
                return;
            }

            // Несистемный том предпочтительнее: на нём меньше постороннего фона.
            int preferred = 0;
            for (int index = 0; index < options.Count; index++)
            {
                if (!options[index].Volume.IsSystem) { preferred = index; break; }
            }

            DiskHintText = "Тест измеряет скорость выбранного накопителя через временный файл на указанном томе.";
            SelectedVolumeOption = options[preferred];
        }

        /// <summary>
        /// Пересобирает список предупреждений и решает, можно ли запускать тест.
        ///
        /// Выбор накопителя перевыставляет и том, поэтому метод легко вызывается несколько
        /// раз внахлёст (сброс тома + выбор нового). Токен гарантирует, что состояние
        /// (WarningTexts/IsRunEnabled) применит только САМЫЙ ПОСЛЕДНИЙ вызов: без него более
        /// медленный устаревший опрос WMI/BitLocker мог бы завершиться позже и перезаписать
        /// уже актуальный результат данными по уже не выбранному тому.
        /// </summary>
        private async Task RefreshWarningsAsync()
        {
            int token = ++_warningsToken;

            if (_selectedVolume == null)
            {
                WarningTexts = Array.Empty<string>();
                _warnings.Clear();
                ShowWarnings = false;
                IsRunEnabled = false;
                return;
            }

            var volume = _selectedVolume;
            bool allowed = BenchmarkWarningService.TryValidateFreeSpace(
                volume, SelectedFileSize, out string blockingError);

            // Опрос состояния шифрования тома занимает у Windows несколько секунд, поэтому
            // объясняем пользователю, почему кнопка запуска пока недоступна.
            if (!_running) RunStatusText = "Проверяем том...";

            var collected = new List<string>();
            try
            {
                collected = await BenchmarkWarningService.CollectAsync(volume);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "BenchmarkViewModel.RefreshWarningsAsync");
            }

            // Пока шёл опрос тома, пользователь мог переключить накопитель — тогда этот
            // результат уже неактуален и трогать интерфейс не должен.
            if (token != _warningsToken) return;

            if (!allowed) collected.Insert(0, blockingError);
            _warnings = collected;

            WarningTexts = _warnings.Select(w => "• " + w).ToList();
            ShowWarnings = _warnings.Count > 0;
            IsRunEnabled = allowed && !_running;

            if (!_running)
            {
                RunStatusText = allowed
                    ? "Тест ещё не запускался"
                    : "Запуск невозможен — смотрите предупреждение выше";
            }
        }
    }
}
