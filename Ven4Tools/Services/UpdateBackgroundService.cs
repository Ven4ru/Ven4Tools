using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Фоновый сервис уведомлений. Раз в несколько часов (при наличии интернета)
    /// проверяет два события и показывает уведомление в трее:
    ///   • есть ли обновления для установленных приложений (winget) — флаг NotifyAppUpdates;
    ///   • появились ли новые приложения в каталоге — флаг NotifyNewApps.
    /// Запускается после показа MainWindow, корректно останавливается через CancellationToken.
    /// </summary>
    public sealed class UpdateBackgroundService : IDisposable
    {
        // Задержка перед первой проверкой — даём приложению спокойно загрузиться.
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
        // Интервал периодических проверок.
        private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

        // Список уже известных приложений каталога — для сравнения при NotifyNewApps.
        private static readonly string KnownAppsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "known_apps.json");

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
            // Параноидальный режим: фоновые проверки обновлений (winget/каталог) отключены.
            if (ProfileService.Current.ParanoidMode) return;
            if (!ConnectivityMonitor.IsOnline)
            {
                await ConnectivityMonitor.CheckAsync();
                if (!ConnectivityMonitor.IsOnline) return;
            }

            var profile = ProfileService.Current;

            if (profile.NotifyAppUpdates)
                await CheckAppUpdatesAsync(ct);

            ct.ThrowIfCancellationRequested();

            if (profile.NotifyNewApps)
                await CheckNewAppsAsync(ct);
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
                    "upgrade --include-unknown --accept-source-agreements --disable-interactivity --locale en-US",
                    TimeSpan.FromMinutes(3));
                ct.ThrowIfCancellationRequested();

                var lines = WingetRunner.StripAnsi(output).Replace("\r", "").Split('\n');
                int sepIdx = Array.FindIndex(lines, l =>
                {
                    string t = l.Trim();
                    return t.Length >= 5 && t.Contains('-') && t.All(c => c == '-' || c == ' ');
                });
                if (sepIdx < 0) return 0;

                int count = 0;
                for (int i = sepIdx + 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) break; // начался футер
                    string t = line.Trim();
                    if (t.All(c => c == '-' || c == ' ')) continue;
                    // Строки-суммарники футера winget («N upgrades available» и т.п.)
                    if (Regex.IsMatch(t, @"^\d+\b.*(package|upgrade)", RegexOptions.IgnoreCase)) break;
                    count++;
                }
                return count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Write($"[UpdateBg] Ошибка winget upgrade: {ex.Message}"); return 0; }
        }

        // ── Новые приложения в каталоге ─────────────────────────────────────────

        private async Task CheckNewAppsAsync(CancellationToken ct)
        {
            // Гарантируем загрузку каталога (online → cache → embedded).
            try { await CatalogLoaderService.PreloadAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Write($"[UpdateBg] Ошибка PreloadAsync: {ex.Message}"); }
            ct.ThrowIfCancellationRequested();

            var catalog = CatalogLoaderService.LoadedCatalog;
            if (catalog == null) return;

            var currentIds = catalog.Apps
                .Select(a => a.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (currentIds.Count == 0) return;

            var known = LoadKnownApps();

            // Первый запуск: эталона ещё нет — просто запоминаем текущий состав,
            // ничего не показываем (иначе уведомили бы обо «всех» приложениях).
            if (known == null)
            {
                SaveKnownApps(currentIds);
                return;
            }

            var newIds = currentIds.Where(id => !known.Contains(id)).ToList();
            if (newIds.Count > 0)
            {
                var names = catalog.Apps
                    .Where(a => newIds.Contains(a.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(a => a.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(3)
                    .ToList();

                string word = Plural(newIds.Count, "приложение", "приложения", "приложений");
                string body = names.Count > 0
                    ? $"В каталоге появилось {newIds.Count} {word}: {string.Join(", ", names)}" +
                      (newIds.Count > names.Count ? " и др." : "")
                    : $"В каталоге появилось {newIds.Count} {word}.";

                ShowNotification("Новые приложения в каталоге", body);
            }

            // Обновляем эталон в любом случае: и новые учли, и удалённые забыли.
            SaveKnownApps(currentIds);
        }

        private static HashSet<string>? LoadKnownApps()
        {
            try
            {
                if (!File.Exists(KnownAppsPath)) return null;
                var ids = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(KnownAppsPath));
                return ids == null ? null : new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { AppLogger.Write($"[UpdateBg] Ошибка чтения known_apps: {ex.Message}"); return null; }
        }

        private static void SaveKnownApps(HashSet<string> ids)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KnownAppsPath)!);
                File.WriteAllText(KnownAppsPath,
                    JsonConvert.SerializeObject(ids.ToList(), Formatting.Indented));
            }
            catch (Exception ex) { AppLogger.Write($"[UpdateBg] Ошибка записи known_apps: {ex.Message}"); }
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
