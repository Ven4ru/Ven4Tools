using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Фоновый сервис уведомлений. Раз в несколько часов (при наличии интернета)
    /// проверяет, есть ли обновления для установленных приложений (winget) —
    /// флаг NotifyAppUpdates — и показывает уведомление в трее.
    /// Запускается после показа MainWindow, корректно останавливается через CancellationToken.
    /// </summary>
    public sealed class UpdateBackgroundService : IDisposable
    {
        // Задержка перед первой проверкой — даём приложению спокойно загрузиться.
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
        // Интервал периодических проверок.
        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        // Кол-во обновлений из прошлой проверки — чтобы не показывать уведомление
        // повторно при каждом цикле, если число не изменилось.
        private int _lastUpgradeCount = -1;

        // ── Уведомления через трей ──────────────────────────────────────────────
        // MainWindow владеет NotifyIcon (MinimizeToTray) и регистрирует здесь
        // делегат показа балуна. Сервис сам трей не создаёт — иначе в трее было бы
        // две иконки. Если трей недоступен, уведомление просто пишется в лог.
        private static Action<string, string>? _notifier;

        /// <summary>Регистрирует обработчик показа уведомления (вызывается из MainWindow).</summary>
        public static void RegisterNotifier(Action<string, string> notifier) => _notifier = notifier;

        /// <summary>Снимает обработчик показа уведомления (при закрытии окна).</summary>
        public static void UnregisterNotifier() => _notifier = null;

        /// <summary>Показывает уведомление: трей-балун, либо запись в лог как fallback.</summary>
        public static void ShowNotification(string title, string body)
        {
            var notifier = _notifier;
            if (notifier != null)
            {
                try { notifier(title, body); return; } catch { }
            }
            // Fallback: без трея модальный MessageBox из фона недопустим (перехватит фокус),
            // поэтому ограничиваемся записью в общий лог приложения.
            AppLogger.Write($"🔔 {title}: {body}");
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
                    catch (Exception ex) { AppLogger.Write($"[UpdateBg] {ex.Message}"); }

                    await Task.Delay(Interval, ct);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка через Dispose */ }
        }

        private async Task CheckOnceAsync(CancellationToken ct)
        {
            // Без интернета и в офлайн-режиме проверки бессмысленны — пропускаем цикл.
            if (OfflineService.IsOffline) return;
            // Параноидальный режим: фоновые проверки обновлений (winget) отключены.
            if (ProfileService.Current.ParanoidMode) return;
            // IsEffectivelyOnline, а не IsOnline: настройка «Принудительный онлайн-режим»
            // обещает игнорировать автодетект сети, но фоновая проверка сверялась с сырым
            // результатом детекта. На VPN/прокси с ложноотрицательным детектом флаг был
            // включён, вкладки оставались видимыми — а уведомления об обновлениях не
            // приходили никогда, без единого следа в журнале.
            if (!ConnectivityMonitor.IsEffectivelyOnline)
            {
                await ConnectivityMonitor.CheckAsync();
                if (!ConnectivityMonitor.IsEffectivelyOnline) return;
            }

            var profile = ProfileService.Current;

            if (profile.NotifyAppUpdates)
                await CheckAppUpdatesAsync(ct);

            if (profile.AutoUpdateApps
                && AutoUpdateService.IsDue(profile.AutoUpdateLastRunUtc, profile.AutoUpdateFrequency, DateTime.UtcNow))
                await RunAutoUpdateAsync(ct);
        }

        // ── Автообновление программ ──────────────────────────────────────────────

        private async Task RunAutoUpdateAsync(CancellationToken ct)
        {
            // Идёт установка из окна — не вклиниваемся, попробуем в следующий цикл.
            if (InstallationService.IsBusy) return;

            AppLogger.Write("⬆ Автообновление программ: проверка");
            var result = await AutoUpdateService.RunAsync(
                ProfileService.Current.AutoUpdateExcluded, AppLogger.Write, ct);
            AppLogger.Write($"⬆ Автообновление программ: {result.Describe()}");

            // winget не ответил — время запуска не записываем: проверка не состоялась,
            // и ждать из-за неё сутки или неделю незачем.
            if (result.WingetUnavailable) return;

            ProfileService.Current.AutoUpdateLastRunUtc = DateTime.UtcNow;
            ProfileService.Save();

            if (result.Updated.Count > 0 || result.Failed.Count > 0)
            {
                ShowNotification("Автообновление программ",
                    result.Failed.Count > 0
                        ? $"Обновлено: {result.Updated.Count}, не удалось: {result.Failed.Count}. Подробности — в журнале."
                        : $"Обновлено программ: {result.Updated.Count}.");
                // Счётчик на «Обзоре» устарел — пересчитываем сразу, а не через три часа.
                try { await CheckAppUpdatesAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AppLogger.Write($"[UpdateBg] {ex.Message}"); }
            }
        }

        // ── Обновления установленных приложений (winget) ────────────────────────

        private async Task CheckAppUpdatesAsync(CancellationToken ct)
        {
            int count = await CountWingetUpgradesAsync(ct);
            ct.ThrowIfCancellationRequested();

            // Уведомляем только при ИЗМЕНЕНИИ числа обновлений относительно прошлой
            // проверки — иначе одно и то же уведомление всплывало бы каждые 3 часа.
            // Сравнение именно на неравенство, а не «стало больше»: после частичного
            // обновления (5 → 3) оставшиеся 3 всё ещё стоит показать, а _lastUpgradeCount
            // при этом обязан уменьшиться, иначе возврат к 5 уже не был бы замечен.
            if (count > 0 && count != _lastUpgradeCount)
            {
                string word = Plural(count, "обновление", "обновления", "обновлений");
                ShowNotification(
                    "Доступны обновления",
                    $"Найдено {count} {word} для установленных приложений. " +
                    "Откройте вкладку «Установленные», чтобы обновить.");
            }
            _lastUpgradeCount = count;
            SetAvailableCount(count);
        }

        /// <summary>
        /// Сколько установленных программ можно обновить по последней фоновой проверке;
        /// -1 — проверка ещё не выполнялась (или выключена в настройках). Нужен экрану
        /// «Обзор»: уведомление в трее показывается один раз, а число должно быть видно
        /// всё время.
        /// </summary>
        public static int AvailableCount { get; private set; } = -1;
        public static event Action? CountChanged;

        private static void SetAvailableCount(int count)
        {
            if (AvailableCount == count) return;
            AvailableCount = count;
            CountChanged?.Invoke();
        }

        // Запускает winget upgrade и считает строки таблицы. Сам разбор — общий
        // Ven4Tools.Shared.WingetOutputParser.ParseUpgradeTableRows (та же петля,
        // что и в SystemViewModel.AppUpdates.ParseUpgradableRows и в лаунчере) —
        // раньше была продублирована здесь отдельной копией цикла.
        private async Task<int> CountWingetUpgradesAsync(CancellationToken ct)
        {
            try
            {
                var (_, output) = await WingetRunner.RunAsync(
                    $"upgrade --include-unknown {WingetArgs.NonInteractiveLine}",
                    TimeSpan.FromMinutes(3));
                ct.ThrowIfCancellationRequested();

                return Ven4Tools.Shared.WingetOutputParser.ParseUpgradeTableRows(output).Count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Write($"[UpdateBg] Ошибка winget upgrade: {ex.Message}"); return 0; }
        }

        // ── Вспомогательное ─────────────────────────────────────────────────────

        // Русское склонение: 1 обновление, 2 обновления, 5 обновлений.
        private static string Plural(int n, string one, string few, string many)
        {
            int mod100 = n % 100;
            int mod10 = n % 10;
            if (mod100 is >= 11 and <= 14) return many;
            return mod10 switch
            {
                1 => one,
                >= 2 and <= 4 => few,
                _ => many
            };
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }
    }
}
