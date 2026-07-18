using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Models;

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
            if (ProfileService.Current.OfflineMode) return;
            // Параноидальный режим: фоновые проверки обновлений (winget) отключены.
            if (ProfileService.Current.ParanoidMode) return;
            if (!ConnectivityMonitor.IsOnline)
            {
                await ConnectivityMonitor.CheckAsync();
                if (!ConnectivityMonitor.IsOnline) return;
            }

            var profile = ProfileService.Current;

            if (profile.NotifyAppUpdates)
                await CheckAppUpdatesAsync(ct);
        }

        // ── Обновления установленных приложений (winget) ────────────────────────

        private async Task CheckAppUpdatesAsync(CancellationToken ct)
        {
            int count = await CountWingetUpgradesAsync(ct);
            ct.ThrowIfCancellationRequested();

            // Уведомляем только при появлении новых обновлений: если число не выросло
            // относительно прошлой проверки — молчим, чтобы не спамить каждые 3 часа.
            if (count > 0 && count != _lastUpgradeCount)
            {
                string word = Plural(count, "обновление", "обновления", "обновлений");
                ShowNotification(
                    "Доступны обновления",
                    $"Найдено {count} {word} для установленных приложений. " +
                    "Откройте вкладку «Установленные», чтобы обновить.");
            }
            _lastUpgradeCount = count;
        }

        // Запускает winget upgrade и считает строки таблицы. Логика парсинга
        // повторяет ту, что используется в лаунчере: считаем строки между
        // разделителем «---» и футером «N upgrades available».
        private async Task<int> CountWingetUpgradesAsync(CancellationToken ct)
        {
            try
            {
                var (_, output) = await WingetRunner.RunAsync(
                    "upgrade --include-unknown --accept-source-agreements --disable-interactivity",
                    TimeSpan.FromMinutes(3));
                ct.ThrowIfCancellationRequested();

                var lines = WingetRunner.StripAnsi(output).Replace("\r", "").Split('\n');
                int sepIdx = Array.FindIndex(lines, WingetRunner.IsTableSeparator);
                if (sepIdx < 0) return 0;

                int count = 0;
                for (int i = sepIdx + 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) break; // начался футер
                    string t = line.Trim();
                    if (WingetRunner.IsTableSeparator(line)) continue;
                    // Строки-суммарники футера winget («N upgrades available» и т.п.)
                    if (Regex.IsMatch(t, @"^\d+\b.*(package|upgrade)", RegexOptions.IgnoreCase)) break;
                    count++;
                }
                return count;
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
