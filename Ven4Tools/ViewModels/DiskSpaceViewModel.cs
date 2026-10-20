using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка категории «Освободить место»: отметка, размер, пояснение и предупреждение.</summary>
    public sealed class CleanupCategoryRow : ViewModelBase
    {
        public CleanupCategoryRow(CleanupCategory category) => Category = category;

        public CleanupCategory Category { get; }
        public string Title => Category.Title;
        public string Description => Category.Description;
        public string? Warning => Category.Warning;
        public bool HasWarning => !string.IsNullOrEmpty(Category.Warning);

        // По умолчанию НИЧЕГО не отмечено: отката у очистки нет.
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (SetField(ref _isSelected, value)) SelectionChanged?.Invoke(); }
        }

        internal Action? SelectionChanged;

        private string _sizeText = "—";
        public string SizeText
        {
            get => _sizeText;
            set => SetField(ref _sizeText, value);
        }

        private string? _note;
        public string? Note
        {
            get => _note;
            set { if (SetField(ref _note, value)) OnPropertyChanged(nameof(HasNote)); }
        }
        public bool HasNote => !string.IsNullOrEmpty(_note);

        /// <summary>Размер в байтах после подсчёта; null — не считали или нет данных.</summary>
        public long? Bytes { get; set; }
        public bool IsUpperBound { get; set; }
    }

    /// <summary>
    /// Раздел «Место и файлы» вкладки «Очистка»: «Освободить место на C» (здесь)
    /// и «Кто держит файл» (<c>DiskSpaceViewModel.FileLocks.cs</c>). Своя ViewModel,
    /// отдельная от <see cref="DebloaterViewModel"/>: вкладкой «Очистка» владеет другой
    /// раздел, стык — одна строка разметки (<c>DiskSpacePanel</c>).
    /// </summary>
    public sealed partial class DiskSpaceViewModel : ViewModelBase
    {
        public ObservableCollection<CleanupCategoryRow> Categories { get; } = new();

        public RelayCommand ScanCommand { get; }
        public RelayCommand CancelScanCommand { get; }
        public RelayCommand CleanCommand { get; }

        private CancellationTokenSource? _scanCts;

        private bool _isScanning;
        public bool IsScanning
        {
            get => _isScanning;
            private set { if (SetField(ref _isScanning, value)) RaiseCommands(); }
        }

        private bool _isCleaning;
        public bool IsCleaning
        {
            get => _isCleaning;
            private set
            {
                if (!SetField(ref _isCleaning, value)) return;
                OnPropertyChanged(nameof(SelectionEnabled));
                RaiseCommands();
            }
        }

        /// <summary>Отметки категорий не меняются, пока идёт очистка.</summary>
        public bool SelectionEnabled => !_isCleaning;

        private bool _scanned;

        private string _statusText = "Нажмите «Посчитать», чтобы узнать, сколько места занимает каждая категория. Ничего не удаляется, пока вы сами не отметите категории и не подтвердите.";
        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        private string _resultText = "";
        public string ResultText
        {
            get => _resultText;
            private set { if (SetField(ref _resultText, value)) OnPropertyChanged(nameof(HasResult)); }
        }
        public bool HasResult => !string.IsNullOrEmpty(_resultText);

        private string _freeSpaceText = "";
        public string FreeSpaceText
        {
            get => _freeSpaceText;
            private set => SetField(ref _freeSpaceText, value);
        }

        public DiskSpaceViewModel()
        {
            foreach (CleanupCategory c in DiskCleanupService.Categories)
                Categories.Add(new CleanupCategoryRow(c) { SelectionChanged = RaiseCommands });

            ScanCommand = RelayCommand.FromAsync(_ => ScanAsync(), _ => !IsScanning && !IsCleaning);
            CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
            CleanCommand = RelayCommand.FromAsync(_ => CleanAsync(),
                _ => _scanned && !IsScanning && !IsCleaning && Categories.Any(c => c.IsSelected));

            InitFileLocks();
            UpdateFreeSpace();
        }

        private void RaiseCommands()
        {
            ScanCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            CleanCommand.RaiseCanExecuteChanged();
        }

        private void UpdateFreeSpace()
        {
            long? free = DiskCleanupService.GetSystemDriveFree();
            FreeSpaceText = free is { } f
                ? string.Format(Tr("Свободно на {0}: {1}"), DiskCleanupService.SystemDriveRoot.TrimEnd('\\'), DiskCleanupService.FormatSize(f))
                : Tr("Свободное место: нет данных");
        }

        // ── Подсчёт ──────────────────────────────────────────────────────────

        private async Task ScanAsync()
        {
            if (IsScanning) return;
            IsScanning = true;
            ResultText = "";
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            CancellationToken ct = _scanCts.Token;
            foreach (CleanupCategoryRow row in Categories)
            {
                row.SizeText = "…";
                row.Note = null;
                row.Bytes = null;
            }
            var measured = new HashSet<CleanupCategoryRow>();

            try
            {
                foreach (CleanupCategoryRow row in Categories)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusText = string.Format(Tr("Считаю: {0}…"), row.Title);
                    row.SizeText = Tr("считаю…");
                    CleanupMeasurement m;
                    try
                    {
                        m = await Task.Run(() => DiskCleanupService.MeasureAsync(row.Category, ct), ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLogger.Write(ex, $"Подсчёт категории {row.Category.Id}");
                        m = new CleanupMeasurement(null, false, null);
                    }
                    ApplyMeasurement(row, m);
                    measured.Add(row);
                }
                StatusText = Tr("Подсчёт завершён. Отметьте, что удалить, и нажмите «Очистить выбранное».");
                _scanned = true;
            }
            catch (OperationCanceledException)
            {
                foreach (CleanupCategoryRow row in Categories.Where(r => !measured.Contains(r)))
                    row.SizeText = "—";
                StatusText = Tr("Подсчёт остановлен.");
                _scanned = Categories.Any(r => r.Bytes != null);
            }
            finally
            {
                IsScanning = false;
                UpdateFreeSpace();
            }
        }

        private static void ApplyMeasurement(CleanupCategoryRow row, CleanupMeasurement m)
        {
            row.Bytes = m.Bytes;
            row.IsUpperBound = m.IsUpperBound;
            row.Note = m.Note;
            row.SizeText = m.Bytes switch
            {
                null => Tr("нет данных"),
                0 => Tr("пусто"),
                { } b when m.IsUpperBound => string.Format(Tr("до {0}"), DiskCleanupService.FormatSize(b)),
                { } b => DiskCleanupService.FormatSize(b),
            };
        }

        // ── Очистка ──────────────────────────────────────────────────────────

        private async Task CleanAsync()
        {
            if (IsCleaning || IsScanning) return;
            List<CleanupCategoryRow> selected = Categories.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0) return;

            // Общий список с предупреждениями — одно подтверждение на весь выбор…
            var text = new StringBuilder();
            text.AppendLine(Tr("Будет удалено:"));
            text.AppendLine();
            foreach (CleanupCategoryRow row in selected)
            {
                text.Append("• ").Append(row.Title);
                if (row.Bytes is > 0) text.Append(" — ").Append(row.SizeText);
                text.AppendLine();
                if (row.HasWarning) text.Append("   ⚠ ").AppendLine(row.Warning);
            }
            text.AppendLine();
            text.Append(Tr("Отменить удаление будет нельзя. Продолжить?"));
            if (MessageBox.Show(text.ToString(), "Освободить место", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            // …и отдельное подтверждение для двух необратимых для самой Windows действий.
            foreach (CleanupCategoryRow row in selected.Where(r => r.Category.Id is "windows-old" or "winsxs"))
            {
                if (MessageBox.Show(string.Format(Tr("{0}\n\n{1}\n\nУдалить?"), row.Title, row.Warning),
                        "Подтвердите удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    != MessageBoxResult.Yes)
                {
                    row.IsSelected = false;
                }
            }
            selected = selected.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return;

            IsCleaning = true;
            ResultText = "";
            long? before = DiskCleanupService.GetSystemDriveFree();
            AppLogger.Write("[Место на диске] Очистка: " + string.Join(", ", selected.Select(r => r.Category.Id)));
            try
            {
                var progress = new Progress<string>(s => StatusText = s);
                await DiskCleanupService.CleanAsync(selected.Select(r => r.Category).ToList(), progress);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "Очистка места на диске");
                StatusText = Tr("Очистка прервалась ошибкой — подробности в журнале.");
            }
            finally
            {
                long? after = DiskCleanupService.GetSystemDriveFree();
                ResultText = BuildResultText(before, after);
                AppLogger.Write("[Место на диске] " + ResultText);
                foreach (CleanupCategoryRow row in selected)
                {
                    row.IsSelected = false;
                    row.SizeText = Tr("очищено — пересчитайте");
                    row.Bytes = null;
                }
                if (StatusText.EndsWith("…", StringComparison.Ordinal))
                    StatusText = Tr("Готово. Нажмите «Посчитать», чтобы увидеть новые размеры.");
                IsCleaning = false;
                UpdateFreeSpace();
            }
        }

        /// <summary>
        /// Итог по реальной разнице свободного места до и после — а не по сумме
        /// подсчитанных размеров: часть файлов могла оказаться занятой, а другие
        /// программы за это время могли что-то записать.
        /// </summary>
        internal static string BuildResultText(long? before, long? after)
        {
            if (before is not { } b || after is not { } a)
                return Tr("Очистка завершена. Сколько освобождено — нет данных: не удалось прочитать свободное место.");
            long freed = a - b;
            string freedText = freed > 0 ? DiskCleanupService.FormatSize(freed) : "0 МБ";
            return string.Format(Tr("Освобождено {0} (свободно было {1}, стало {2})."),
                freedText, DiskCleanupService.FormatSize(b), DiskCleanupService.FormatSize(a));
        }
    }
}
