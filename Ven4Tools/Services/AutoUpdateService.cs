using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Shared;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Автообновление установленных программ через winget.
    ///
    /// Выключено по умолчанию. Включённое, работает, пока клиент запущен (в том числе
    /// свёрнутый в трей): фоновая служба раз в сутки или в неделю обновляет всё, для
    /// чего winget видит новую версию, кроме программ из списка исключений. Отдельной
    /// задачи в планировщике Windows нет намеренно: она запускала бы клиент с правами
    /// администратора без запроса UAC из папки, доступной пользователю на запись, —
    /// готовый способ повысить права для любого процесса этого пользователя.
    ///
    /// Разовое обновление по команде: <c>Ven4Tools.exe --update-apps --silent</c>.
    /// </summary>
    public static class AutoUpdateService
    {
        public const string Daily = "daily";
        public const string Weekly = "weekly";

        public sealed class Result
        {
            public List<string> Updated { get; } = new();
            public List<(string Id, string Reason)> Failed { get; } = new();
            /// <summary>Есть обновление, но программа в списке исключений.</summary>
            public List<string> Excluded { get; } = new();
            /// <summary>winget не найден или не ответил — проверка не состоялась.</summary>
            public bool WingetUnavailable { get; set; }
            public bool Cancelled { get; set; }

            public string Describe()
            {
                if (WingetUnavailable) return "winget не найден или не ответил — обновления не проверены";
                if (Updated.Count == 0 && Failed.Count == 0)
                    return Excluded.Count == 0
                        ? "обновлять нечего — всё актуально"
                        : $"обновлять нечего; в исключениях с обновлением: {Excluded.Count}";
                return $"обновлено {Updated.Count}, не удалось {Failed.Count}" +
                       (Excluded.Count > 0 ? $", пропущено по исключениям {Excluded.Count}" : "");
            }
        }

        /// <summary>Пора ли обновлять: с прошлого запуска прошли сутки или неделя.</summary>
        public static bool IsDue(DateTime? lastRunUtc, string? frequency, DateTime nowUtc)
        {
            if (lastRunUtc == null) return true;
            // Часы перевели назад или профиль пришёл с другой машины — «прошлый запуск»
            // в будущем не должен откладывать обновление навсегда.
            if (lastRunUtc.Value > nowUtc) return true;

            TimeSpan period = string.Equals(frequency, Daily, StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromDays(1)
                : TimeSpan.FromDays(7);
            return nowUtc - lastRunUtc.Value >= period;
        }

        /// <summary>
        /// Список исключений из текста настроек: идентификаторы через запятую, пробел
        /// или с новой строки. Мусор отбрасывается — по нему всё равно ничего не найти.
        /// </summary>
        public static List<string> ParseExcluded(string? text) =>
            (text ?? "")
                .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Разбор таблицы «winget upgrade». Обёртка над общим разбором: тот же исходный
        /// файл подключён и в лаунчер, поэтому тестам нужна точка входа из клиента.
        /// </summary>
        internal static List<WingetOutputParser.UpgradeEntry> ParseUpgrades(string rawOutput) =>
            WingetOutputParser.ParseUpgradeEntries(rawOutput);

        /// <summary>Делит найденные обновления на те, что ставим, и исключённые.</summary>
        internal static (List<WingetOutputParser.UpgradeEntry> Targets, List<string> Excluded) SelectTargets(
            IEnumerable<WingetOutputParser.UpgradeEntry> entries, IEnumerable<string> excludedIds)
        {
            var excluded = new HashSet<string>(excludedIds, StringComparer.OrdinalIgnoreCase);
            var targets = new List<WingetOutputParser.UpgradeEntry>();
            var skipped = new List<string>();
            foreach (var entry in entries)
            {
                if (excluded.Contains(entry.Id)) skipped.Add(entry.Id);
                else targets.Add(entry);
            }
            return (targets, skipped);
        }

        /// <summary>
        /// Проверяет обновления и ставит их по одному — так сбой одной программы не
        /// останавливает остальные, а в журнале видно, что именно не обновилось.
        /// </summary>
        public static async Task<Result> RunAsync(
            IReadOnlyCollection<string> excludedIds, Action<string> log, CancellationToken ct)
        {
            var result = new Result();

            // Без --include-unknown: программу с неопределяемой версией winget предлагал бы
            // «обновить» в каждом цикле, и она переустанавливалась бы снова и снова.
            // Вручную такие программы обновляются на вкладке «Установленные».
            var (code, output) = await WingetRunner.RunAsync(
                $"upgrade --source winget {WingetArgs.NonInteractiveLine}",
                TimeSpan.FromMinutes(3));
            ct.ThrowIfCancellationRequested();

            var entries = ParseUpgrades(output);
            if (code == -1 && entries.Count == 0)
            {
                result.WingetUnavailable = true;
                return result;
            }

            var (targets, excluded) = SelectTargets(entries, excludedIds);
            result.Excluded.AddRange(excluded);
            if (targets.Count == 0) return result;

            log($"⬆ Автообновление: к обновлению {targets.Count}" +
                (excluded.Count > 0 ? $", пропущено по исключениям {excluded.Count}" : ""));

            foreach (var entry in targets)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

                // Общий семафор с каталогом и «Установленными»: два установщика разом
                // дают ошибку 1618 у обоих.
                await InstallationService.InstallSemaphore.WaitAsync(ct);
                try
                {
                    log($"⬆ {entry.Name}: {entry.Version} → {entry.Available}");
                    int exit = await WingetRunner.RunStreamingAsync(
                        $"upgrade --id \"{entry.Id}\" --exact --silent {WingetArgs.ModifyLine}",
                        line => AppLogger.Write($"  {line}"), TimeSpan.FromMinutes(15));

                    if (exit == 0 || WingetErrorMapper.IsSuccessWithReboot(exit))
                    {
                        result.Updated.Add(entry.Id);
                        log($"✅ {entry.Name} обновлён");
                    }
                    else
                    {
                        string reason = exit == -1 ? "winget не ответил вовремя" : WingetErrorMapper.MapExitCode(exit);
                        result.Failed.Add((entry.Id, reason));
                        log($"⚠ {entry.Name}: {reason}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Failed.Add((entry.Id, ex.Message));
                    log($"❌ {entry.Name}: {ex.Message}");
                }
                finally
                {
                    InstallationService.InstallSemaphore.Release();
                }
            }

            return result;
        }
    }
}
