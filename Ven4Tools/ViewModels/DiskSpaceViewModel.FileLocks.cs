using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка списка «Кто держит файл».</summary>
    public sealed class LockingProcessRow
    {
        public LockingProcessRow(LockingProcess process) => Process = process;

        public LockingProcess Process { get; }
        public string Name => Process.Name;
        public int ProcessId => Process.ProcessId;
        public string ExePath => Process.ExePath ?? Tr("путь недоступен");
        public string Details => Process.RunsFromTarget
            ? string.Format(Tr("PID {0} · запущена из этой папки"), Process.ProcessId)
            : string.Format(Tr("PID {0}"), Process.ProcessId);

        /// <summary>Системным процессам и службам кнопку «Завершить» не показываем.</summary>
        public bool CanTerminate => !Process.IsSystem;
        public bool IsSystem => Process.IsSystem;
    }

    public sealed partial class DiskSpaceViewModel
    {
        public ObservableCollection<LockingProcessRow> LockingProcesses { get; } = new();

        public RelayCommand BrowseLockFileCommand { get; private set; } = null!;
        public RelayCommand BrowseLockFolderCommand { get; private set; } = null!;
        public RelayCommand FindLocksCommand { get; private set; } = null!;
        public RelayCommand CancelFindLocksCommand { get; private set; } = null!;
        public RelayCommand CloseLockingWindowCommand { get; private set; } = null!;
        public RelayCommand TerminateLockingCommand { get; private set; } = null!;

        private CancellationTokenSource? _locksCts;

        private string _lockPath = "";
        public string LockPath
        {
            get => _lockPath;
            set { if (SetField(ref _lockPath, value)) FindLocksCommand.RaiseCanExecuteChanged(); }
        }

        private bool _isFindingLocks;
        public bool IsFindingLocks
        {
            get => _isFindingLocks;
            private set
            {
                if (!SetField(ref _isFindingLocks, value)) return;
                FindLocksCommand.RaiseCanExecuteChanged();
                CancelFindLocksCommand.RaiseCanExecuteChanged();
            }
        }

        private string _locksStatusText = "Выберите файл или папку, которые не удаётся удалить или переименовать.";
        public string LocksStatusText
        {
            get => _locksStatusText;
            private set => SetField(ref _locksStatusText, value);
        }

        private void InitFileLocks()
        {
            BrowseLockFileCommand = new RelayCommand(_ => BrowseFile(), _ => !IsFindingLocks);
            BrowseLockFolderCommand = new RelayCommand(_ => BrowseFolder(), _ => !IsFindingLocks);
            FindLocksCommand = RelayCommand.FromAsync(_ => FindLocksAsync(),
                _ => !IsFindingLocks && !string.IsNullOrWhiteSpace(LockPath));
            CancelFindLocksCommand = new RelayCommand(_ => _locksCts?.Cancel(), _ => IsFindingLocks);
            CloseLockingWindowCommand = RelayCommand.FromAsync(p => CloseWindowAsync(p as LockingProcessRow));
            TerminateLockingCommand = RelayCommand.FromAsync(p => TerminateAsync(p as LockingProcessRow),
                p => p is LockingProcessRow { CanTerminate: true });
        }

        // Перетаскивание из Проводника сюда не работает: клиент запущен от имени
        // администратора, а Windows (UIPI) не пускает перетаскивание из обычного
        // процесса в повышенный. Поэтому только диалоги и поле для вставки пути.
        private void BrowseFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Tr("Какой файл не удаляется?"),
                CheckFileExists = true,
                // Без этого диалог открыл бы ярлык, а не сам .lnk-файл.
                DereferenceLinks = false,
            };
            if (dlg.ShowDialog() == true)
            {
                LockPath = dlg.FileName;
                FindLocksCommand.Execute(null);
            }
        }

        private void BrowseFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Tr("Какая папка не удаляется?") };
            if (dlg.ShowDialog() == true)
            {
                LockPath = dlg.FolderName;
                FindLocksCommand.Execute(null);
            }
        }

        private async Task FindLocksAsync()
        {
            if (IsFindingLocks) return;
            string path = LockPath.Trim().Trim('"');
            if (path.Length == 0) return;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                LocksStatusText = Tr("Такого файла или папки нет. Проверьте путь.");
                LockingProcesses.Clear();
                return;
            }

            IsFindingLocks = true;
            LockingProcesses.Clear();
            bool isFolder = Directory.Exists(path);
            LocksStatusText = isFolder ? Tr("Проверяю файлы папки…") : Tr("Проверяю…");
            _locksCts?.Dispose();
            _locksCts = new CancellationTokenSource();
            CancellationToken ct = _locksCts.Token;
            try
            {
                FileLockResult result = await Task.Run(() => FileLockService.Find(path, ct), ct);
                foreach (LockingProcess p in result.Processes)
                    LockingProcesses.Add(new LockingProcessRow(p));

                string status = result.Processes.Count == 0
                    ? Tr("Никто из запущенных программ этот файл не держит. Если он всё равно не удаляется — дело в правах доступа или файл занят на уровне драйвера.")
                    : string.Format(Tr("Держат программ: {0}. Сначала попробуйте «Закрыть окно» — программа сможет сохранить данные."), result.Processes.Count);
                if (isFolder)
                    status += " " + string.Format(Tr("Проверено файлов: {0}."), result.FilesChecked);
                if (result.Truncated)
                    status += " " + string.Format(Tr("Файлов в папке больше {0} — проверены только первые."), FileLockService.MaxFolderFiles);
                LocksStatusText = status;
            }
            catch (OperationCanceledException)
            {
                LocksStatusText = Tr("Проверка остановлена.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                AppLogger.Write($"[Кто держит файл] {ex.GetType().Name}: {ex.Message}");
                LocksStatusText = Tr("Не удалось проверить: Windows отказала в доступе. Подробности — в журнале.");
            }
            finally
            {
                IsFindingLocks = false;
            }
        }

        /// <summary>Вежливо: WM_CLOSE главному окну. Программа сама спросит про несохранённое.</summary>
        private async Task CloseWindowAsync(LockingProcessRow? row)
        {
            if (row == null) return;
            using Process? proc = FileLockService.OpenSameProcess(row.Process);
            if (proc == null)
            {
                LocksStatusText = string.Format(Tr("Программа {0} уже закрыта."), row.Name);
                await RefreshLocksAsync();
                return;
            }

            bool sent;
            try { sent = proc.CloseMainWindow(); }
            catch (InvalidOperationException) { sent = false; }
            if (!sent)
            {
                MessageBox.Show(
                    string.Format(Tr("У программы {0} нет окна, которое можно закрыть. Закройте её сами (например, через значок в трее) или нажмите «Завершить»."), row.Name),
                    "Закрыть окно", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LocksStatusText = string.Format(Tr("Попросил {0} закрыться. Если программа спросит о сохранении — ответьте ей."), row.Name);
            // Даём программе время на диалог «Сохранить?» и на выход, затем перепроверяем.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await proc.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) { }
            await RefreshLocksAsync();
        }

        private async Task TerminateAsync(LockingProcessRow? row)
        {
            if (row == null || !row.CanTerminate) return;
            if (MessageBox.Show(
                    string.Format(Tr("Завершить {0} (PID {1}) принудительно?\n\nВсё несохранённое в этой программе будет потеряно. Сначала лучше попробовать «Закрыть окно»."),
                        row.Name, row.ProcessId),
                    "Завершить программу", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes)
                return;

            using Process? proc = FileLockService.OpenSameProcess(row.Process);
            if (proc == null)
            {
                LocksStatusText = string.Format(Tr("Программа {0} уже закрыта."), row.Name);
                await RefreshLocksAsync();
                return;
            }
            try
            {
                proc.Kill();
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await proc.WaitForExitAsync(wait.Token);
                AppLogger.Write($"[Кто держит файл] Завершён процесс {row.Name} (PID {row.ProcessId})");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLogger.Write($"[Кто держит файл] Не удалось завершить {row.Name}: {ex.Message}");
                MessageBox.Show(string.Format(Tr("Не удалось завершить {0}: Windows отказала в доступе."), row.Name),
                    "Завершить программу", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await RefreshLocksAsync();
        }

        private Task RefreshLocksAsync() =>
            string.IsNullOrWhiteSpace(LockPath) ? Task.CompletedTask : FindLocksAsync();
    }
}
