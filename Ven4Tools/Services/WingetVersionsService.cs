using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class WingetVersionsService
    {
        // Без кеша каждая загрузка/обновление каталога порождала `winget show --versions`
        // на КАЖДОЕ приложение с AlternativeId (десятки записей в каталоге v13) —
        // десятки секунд непрерывного порождения процессов на одно нажатие «Обновить
        // каталог». TTL 30 минут — тот же порядок, что и у соседнего AvailabilityChecker
        // (5 минут), но версии пакетов меняются реже доступности, поэтому окно шире.
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, (DateTime FetchedAt, List<string> Versions)> _cache = new();

        public static async Task<List<string>> FetchVersionsAsync(string wingetId, CancellationToken token = default)
        {
            if (!CommandLineGuard.ValidateId(wingetId)) return new List<string>();

            if (_cache.TryGetValue(wingetId, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheDuration)
                return cached.Versions;

            try
            {
                // WingetArgs.Query добавляет --accept-source-agreements/--disable-interactivity:
                // без них winget на машине, где соглашение источника ещё не принято
                // (свежая Windows — типовой сценарий для установщика ПО), вместо списка
                // версий печатает запрос подтверждения и завершается ошибкой — список
                // версий молча оказывался пустым, и выбор версии в каталоге не работал.
                var (_, output) = await WingetRunner.RunAsync(
                    WingetArgs.Query("show", "--id", wingetId, "--versions", "-e", "--source", "winget"),
                    token: token);

                var versions = ParseVersions(output);
                _cache[wingetId] = (DateTime.UtcNow, versions);
                return versions;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WingetVersionsService] Получение списка версий «{wingetId}»: {ex.Message}");
                return new List<string>();
            }
        }

        // Вызывать только на явное действие пользователя («Обновить каталог»), не
        // при первичной загрузке — иначе кеш терял бы весь смысл на каждом старте.
        public static void ClearCache() => _cache.Clear();

        private static List<string> ParseVersions(string output)
        {
            // ANSI-последовательности убираем перед разбором — как и остальные
            // разборы вывода winget. Без этого escape-код в начале строки-разделителя
            // не давал WingetRunner.IsTableSeparator её распознать, pastSeparator
            // никогда не выставлялся, и список версий оказывался пустым.
            var lines = WingetRunner.StripAnsi(output)
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            bool pastSeparator = false;
            var versions = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!pastSeparator)
                {
                    if (WingetRunner.IsTableSeparator(line)) pastSeparator = true;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(trimmed))
                    versions.Add(trimmed);
            }

            return versions;
        }
    }
}
