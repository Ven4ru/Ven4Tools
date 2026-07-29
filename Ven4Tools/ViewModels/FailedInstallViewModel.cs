using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна неуспешная установка в сводке после пакетной установки: что не встало,
    /// каким способом пробовали и по какой причине (данные берутся из журнала
    /// <see cref="InstallFailureService"/>, который до этого читал только лаунчер).
    /// <para>Сам повтор выполняет владелец списка (<see cref="CatalogViewModel"/>) —
    /// тем же <c>InstallationService.InstallAppAsync</c>, что и обычная установка;
    /// эта модель только хранит текст, состояние и гейт занятости.</para>
    /// </summary>
    public sealed class FailedInstallViewModel : INotifyPropertyChanged
    {
        private readonly Func<FailedInstallViewModel, Task> _retry;

        public FailedInstallViewModel(
            string displayName, string method, string error, Func<FailedInstallViewModel, Task> retry)
        {
            DisplayName = displayName;
            _method = method;
            _error = error;
            _retry = retry;
            RetryCommand = new RelayCommand(
                async _ => await ExecuteRetryAsync(),
                _ => InstallFailureReport.CanRetry(IsRetrying));
        }

        public string DisplayName { get; }

        private string _method;
        public string Method
        {
            get => _method;
            private set { if (SetField(ref _method, value)) OnPropertyChanged(nameof(MethodText)); }
        }

        public string MethodText => $"Способ: {Method}";

        private string _error;
        public string Error
        {
            get => _error;
            private set => SetField(ref _error, value);
        }

        private bool _isRetrying;
        public bool IsRetrying
        {
            get => _isRetrying;
            private set { if (SetField(ref _isRetrying, value)) RetryCommand.RaiseCanExecuteChanged(); }
        }

        private string _retryStatus = "";
        /// <summary>Статус текущего/последнего повтора — пусто, пока повтор не запускали.</summary>
        public string RetryStatus
        {
            get => _retryStatus;
            set => SetField(ref _retryStatus, value);
        }

        public RelayCommand RetryCommand { get; }

        /// <summary>Обновляет способ и причину после неудачного повтора.</summary>
        public void UpdateFailure(string method, string error)
        {
            Method = method;
            Error = error;
        }

        private async Task ExecuteRetryAsync()
        {
            // Тот же гейт занятости, что у всех остальных путей установки: общий
            // InstallSemaphore. Проверка здесь дублирует CanExecute намеренно —
            // команду можно выполнить и программно, а параллельный msiexec даёт
            // ошибку Windows Installer 1618.
            if (!InstallFailureReport.CanRetry(IsRetrying)) return;

            IsRetrying = true;
            try
            {
                await _retry(this);
            }
            catch (Exception ex)
            {
                // Команда вызывается как async void — необработанное исключение здесь
                // уронило бы всё приложение.
                RetryStatus = $"❌ {ex.Message}";
                AppLogger.Write(ex, "Ошибка повторной установки");
            }
            finally
            {
                IsRetrying = false;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
