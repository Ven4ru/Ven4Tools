using System;
using System.Threading;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Общее для долгих системных действий вкладки (.NET 3.5, проверка системных
    /// файлов, сброс Центра обновления): одно действие за раз, отмена, проверка, что
    /// не идёт установка патчей или чужой DISM/SFC.
    /// </summary>
    public sealed partial class WindowsUpdateViewModel
    {
        private CancellationTokenSource? _systemTaskCts;

        public RelayCommand CancelSystemTaskCommand { get; private set; } = null!;

        private bool _isSystemTaskRunning;
        public bool IsSystemTaskRunning
        {
            get => _isSystemTaskRunning;
            private set
            {
                if (!SetField(ref _isSystemTaskRunning, value)) return;
                OnPropertyChanged(nameof(IsSystemTaskIdle));
                UpdateSelectionSummary();
                CommandManagerRequery();
            }
        }

        /// <summary>Для IsEnabled кнопок системных действий.</summary>
        public bool IsSystemTaskIdle => !IsSystemTaskRunning;

        private bool _isSystemTaskCancellable;
        /// <summary>Сброс Центра обновления не отменяется (откат делает он сам), DISM и SFC — да.</summary>
        public bool IsSystemTaskCancellable { get => _isSystemTaskCancellable; private set => SetField(ref _isSystemTaskCancellable, value); }

        private void InitServicingCommands()
        {
            CancelSystemTaskCommand = new RelayCommand(_ => CancelSystemTask());
        }

        private static void CommandManagerRequery() =>
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        /// <summary>
        /// Можно ли начинать системное действие. Причину отказа показывает сам.
        /// needsServicingExclusive — действие запускает DISM/SFC (второй такой процесс
        /// Windows одновременно не выполняет).
        /// </summary>
        private bool CanStartSystemTask(string caption, bool needsServicingExclusive)
        {
            if (IsSystemTaskRunning) return false;
            if (IsInstalling || WindowsUpdateService.IsBusy)
            {
                MessageBox.Show("Сейчас идёт установка (обновлений Windows или приложений). Дождитесь её завершения и повторите.",
                    caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (needsServicingExclusive && ServicingProcessRunner.FindRunningServicingTool() is { } running)
            {
                MessageBox.Show($"Уже запущено обслуживание Windows ({running}). Дождитесь его завершения и повторите.",
                    caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        private CancellationToken BeginSystemTask(bool cancellable)
        {
            _systemTaskCts?.Dispose();
            _systemTaskCts = new CancellationTokenSource();
            IsSystemTaskCancellable = cancellable;
            IsSystemTaskRunning = true;
            return _systemTaskCts.Token;
        }

        private void EndSystemTask()
        {
            IsSystemTaskRunning = false;
            IsSystemTaskCancellable = false;
        }

        private void CancelSystemTask()
        {
            if (!IsSystemTaskRunning || !IsSystemTaskCancellable || _systemTaskCts == null) return;
            var answer = MessageBox.Show(
                "Прервать операцию?\n\n" +
                "DISM или проверка системных файлов будут остановлены. Прерванная на середине операция может " +
                "оставить компонент в промежуточном состоянии — обычно Windows доводит или откатывает его при следующей перезагрузке.",
                "Прервать", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            try { _systemTaskCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
