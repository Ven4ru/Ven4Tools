using System;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Фоновая проверка обновлений Windows — по аналогии с UpdateBackgroundService
    /// (приложения из winget). Режим поведения — из ProfileService.Current.WindowsUpdateMode:
    ///   "NotSet"            — проверка не выполняется вообще (первый вход ещё не пройден).
    ///   "NotifyOnly"        — только уведомление + бейдж-счётчик.
    ///   "NotifyAndDownload" — то же + тихое скачивание в фоне (без установки).
    /// Никогда не устанавливает патчи автоматически — это всегда явный клик пользователя.
    /// </summary>
    public sealed class WindowsUpdateBackgroundService : IDisposable
    {
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        private readonly WindowsUpdateService _service;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public static int AvailableCount { get; private set; }
        public static event Action? CountChanged;

        public WindowsUpdateBackgroundService(WindowsUpdateService? service = null)
        {
            _service = service ?? new WindowsUpdateService();
        }

        public void Start()
        {
            if (_loop != null) return;
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(FirstDelay, ct);
                while (!ct.IsCancellationRequested)
                {
                    try { await CheckOnceAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AppLogger.Write($"[WindowsUpdateBg] {ex.Message}"); }

                    await Task.Delay(Interval, ct);
                }
            }
            catch (OperationCanceledException) { /* штатна остановка через Dispose */ }
        }

        internal async Task CheckOnceAsync(CancellationToken ct)
        {
            var mode = ProfileService.Current.WindowsUpdateMode;
            if (mode == "NotSet") return;
            if (ProfileService.Current.ParanoidMode) return;
            if (ProfileService.Current.OfflineMode) return;
            if (!ConnectivityMonitor.IsOnline)
            {
                await ConnectivityMonitor.CheckAsync();
                if (!ConnectivityMonitor.IsOnline) return;
            }

            var result = await _service.SearchAsync(ct);
            if (!result.Success) return;

            SetCount(result.Items.Count);

            if (result.Items.Count > 0)
            {
                UpdateBackgroundService.ShowNotification(
                    "Доступны обновления Windows",
                    $"Найдено {result.Items.Count} патчей. Откройте вкладку «Windows Update», чтобы выбрать и установить.");
            }

            if (mode == "NotifyAndDownload" && result.Items.Count > 0 && !WindowsUpdateService.IsBusy)
            {
                // Тихое скачивание без установки: захватываем прогресс, но не показываем UI.
                // InstallSelectedAsync тут не используется намеренно — он и скачивает, и ставит;
                // отдельного "только скачать" метода источник (Task 6/7) пока не предоставляет,
                // поэтому в первой версии фоновый режим ограничивается уведомлением до тех пор,
                // пока IWindowsUpdateSource не получит DownloadOnlyAsync (см. заметку ниже).
                AppLogger.Write("[WindowsUpdateBg] Режим NotifyAndDownload: фоновое скачивание запланировано, но метод DownloadOnlyAsync ещё не реализован — пока только уведомление.");
            }
        }

        private static void SetCount(int count)
        {
            if (AvailableCount == count) return;
            AvailableCount = count;
            CountChanged?.Invoke();
        }

        // Только для тестов: xUnit по умолчанию не гарантирует порядок между классами,
        // а AvailableCount — static на весь процесс. Без сброса тесты влияли бы друг на друга.
        internal static void CountChangedResetForTests() => AvailableCount = 0;

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }
    }
}
