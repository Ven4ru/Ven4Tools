using System;
using System.Collections.Generic;
using System.Linq;
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
    ///   "NotifyAndDownload" — то же уведомление плюс тихое скачивание найденных патчей
    ///                         в фоне (WindowsUpdateService.DownloadOnlyAsync): файлы
    ///                         ложатся в кэш Windows Update, и последующая установка по
    ///                         клику пользователя стартует сразу, без ожидания загрузки.
    ///                         Скачиваются только ещё не скачанные патчи (IsDownloaded),
    ///                         так что повторные проверки каждые 6 часов не качают одно
    ///                         и то же заново. По завершении показывается отдельное
    ///                         уведомление «скачаны и готовы к установке».
    /// Никогда не устанавливает патчи автоматически — это всегда явный клик пользователя.
    /// Скачивание системы не меняет: DownloadOnlyAsync по контракту не вызывает установщик.
    /// </summary>
    public sealed class WindowsUpdateBackgroundService : IDisposable
    {
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        private readonly WindowsUpdateService _service;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        // Кол-во найденных патчей из прошлой проверки — чтобы не показывать
        // уведомление повторно при каждом цикле, если число не изменилось.
        // Тот же приём, что _lastUpgradeCount в UpdateBackgroundService (и
        // LastNotified*-поля фонового сервиса лаунчера): без него одно и то же
        // «Доступны обновления Windows» всплывало бы каждые 6 часов до тех пор,
        // пока пользователь не установит патчи.
        private int _lastNotifiedCount = -1;

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
            catch (OperationCanceledException) { /* штатная остановка через Dispose */ }
        }

        internal async Task CheckOnceAsync(CancellationToken ct)
        {
            var mode = ProfileService.Current.WindowsUpdateMode;
            if (mode == "NotSet") return;
            if (ProfileService.Current.ParanoidMode) return;
            if (ProfileService.Current.OfflineMode) return;
            // IsEffectivelyOnline, а не IsOnline — та же причина, что и в
            // UpdateBackgroundService: «Принудительный онлайн-режим» должен перекрывать
            // автодетект сети и здесь, иначе на VPN с ложноотрицательным детектом
            // фоновый поиск обновлений Windows молча не запускается.
            if (!ConnectivityMonitor.IsEffectivelyOnline)
            {
                await ConnectivityMonitor.CheckAsync();
                if (!ConnectivityMonitor.IsEffectivelyOnline) return;
            }

            var result = await _service.SearchAsync(ct);
            if (!result.Success) return;

            SetCount(result.Items.Count);

            if (result.Items.Count > 0 && result.Items.Count != _lastNotifiedCount)
            {
                UpdateBackgroundService.ShowNotification(
                    "Доступны обновления Windows",
                    $"Найдено {result.Items.Count} патчей. Откройте вкладку «Windows Update», чтобы выбрать и установить.");
            }
            _lastNotifiedCount = result.Items.Count;

            // Гейт только по своим WU-операциям: идущая установка приложений из
            // каталога скачиванию патчей в кэш Windows Update не мешает и мешать
            // не должна (msiexec и загрузчик WUA — разные подсистемы).
            if (mode == "NotifyAndDownload" && result.Items.Count > 0 && !WindowsUpdateService.IsWindowsUpdateBusy)
                await DownloadInBackgroundAsync(result.Items, ct);
        }

        /// <summary>
        /// Тихо скачивает найденные патчи, ничего не устанавливая. Вызывается только в
        /// режиме "NotifyAndDownload". InstallSelectedAsync здесь не используется
        /// принципиально — он и скачивает, и ставит; фоновому режиму разрешено только
        /// первое, установка остаётся за явным кликом пользователя.
        /// </summary>
        private async Task DownloadInBackgroundAsync(
            IReadOnlyList<WindowsUpdateItem> items, CancellationToken ct)
        {
            // Уже скачанные патчи (WUA помечает их IsDownloaded на уровне ОС, пометка
            // переживает перезапуск клиента) исключаются из списка — иначе каждая
            // проверка раз в 6 часов запускала бы бессмысленный проход по кэшу.
            var pending = items.Where(i => !i.IsDownloaded).Select(i => i.UpdateId).Distinct().ToList();
            if (pending.Count == 0)
            {
                AppLogger.Write("[WindowsUpdateBg] Все найденные патчи уже скачаны — фоновая загрузка не требуется.");
                return;
            }

            AppLogger.Write($"[WindowsUpdateBg] Фоновое скачивание: {pending.Count} патчей.");
            var outcome = await _service.DownloadOnlyAsync(pending, NoProgress.Instance, ct);

            int downloaded = outcome.Items.Count(o => o.Success);
            if (downloaded == 0)
            {
                // Фоновая операция не должна тревожить пользователя своими сбоями: он о
                // ней не просил в этот момент и ничего исправить не может. Пишем в лог и
                // ждём следующей проверки — она повторит попытку с теми же патчами.
                // Уведомление «Доступны обновления Windows» выше пользователь уже получил,
                // так что без информации он не остался.
                AppLogger.Write($"[WindowsUpdateBg] Фоновое скачивание не удалось: {outcome.ErrorMessage}");
                return;
            }

            // Отдельное уведомление от «Доступны обновления Windows»: смысл другой —
            // патчи уже лежат на диске, установка пойдёт мгновенно. Повторов не будет:
            // на следующей проверке эти патчи придут с IsDownloaded=true и в pending
            // не попадут, то есть сообщение появляется ровно один раз на партию.
            UpdateBackgroundService.ShowNotification(
                "Обновления Windows скачаны в фоне",
                $"{downloaded} патчей загружены и готовы к установке. Откройте вкладку «Windows Update» — установка начнётся сразу, без ожидания загрузки.");

            if (downloaded < outcome.Items.Count)
                AppLogger.Write($"[WindowsUpdateBg] Скачано {downloaded} из {outcome.Items.Count} патчей — остальные будут повторены при следующей проверке.");
        }

        /// <summary>
        /// Заглушка IProgress для фоновой загрузки: прогресс скачивания никуда не
        /// выводится (UI вкладки в этот момент не задействован), а обычный Progress&lt;T&gt;
        /// без обработчика всё равно планировал бы задачу в пул на каждый отчёт.
        /// </summary>
        private sealed class NoProgress : IProgress<WindowsUpdateProgress>
        {
            public static readonly NoProgress Instance = new();
            public void Report(WindowsUpdateProgress value) { }
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
