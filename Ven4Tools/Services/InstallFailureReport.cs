using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Разбор журнала неудачных установок (<see cref="InstallFailureService"/>) для
    /// показа пользователю: подбор записи под конкретное приложение, читаемое название
    /// способа установки и общее правило доступности повтора.
    /// <para>Только чтение и интерпретация — формат файла не меняется, он остаётся
    /// общим контрактом с лаунчером.</para>
    /// </summary>
    public static class InstallFailureReport
    {
        /// <summary>
        /// Самая свежая запись журнала по приложению <paramref name="appId"/>, сделанная
        /// не раньше <paramref name="sinceUtc"/> (начала текущей пакетной установки).
        /// <para>Записи с неразбираемой меткой времени пропускаются: подтвердить, что они
        /// относятся к текущей попытке, невозможно, а показать причину годовой давности
        /// хуже, чем не показать ничего.</para>
        /// </summary>
        public static InstallFailure? FindLatest(
            IEnumerable<InstallFailure>? failures, string? appId, DateTime sinceUtc)
        {
            if (failures == null || string.IsNullOrEmpty(appId)) return null;

            InstallFailure? best = null;
            DateTime bestMoment = DateTime.MinValue;

            foreach (var failure in failures)
            {
                if (failure == null) continue;
                if (!string.Equals(failure.AppId, appId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryParseUtc(failure.Timestamp, out var moment)) continue;
                if (moment < sinceUtc) continue;
                // При равных метках времени выигрывает более поздняя запись в файле:
                // журнал дописывается в конец, значит она и есть последняя попытка.
                if (best != null && moment < bestMoment) continue;

                best = failure;
                bestMoment = moment;
            }

            return best;
        }

        /// <summary>Человекочитаемое название способа установки из поля Method журнала.</summary>
        public static string MethodLabel(string? method)
        {
            string key = (method ?? "").Trim();
            return key.ToLowerInvariant() switch
            {
                "winget"      => "Winget",
                "choco"       => "Chocolatey",
                "direct"      => "Прямая ссылка",
                "local"       => "Локальный установщик",
                "cache"       => "Офлайн-кэш",
                "all-sources" => "Все источники",
                "validation"  => "Проверка идентификатора",
                ""            => "Неизвестен",
                _             => key
            };
        }

        /// <summary>
        /// Правило доступности кнопки «Повторить»: повтор запрещён, пока занят общий
        /// семафор установки (<see cref="InstallationService.IsBusy"/> — тот же гейт,
        /// что у каталога, карточки, истории и Windows Update) либо пока уже идёт
        /// повтор этой же записи.
        /// </summary>
        public static bool CanRetry(bool retryInProgress)
            => !retryInProgress && !InstallationService.IsBusy;

        private static bool TryParseUtc(string? value, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed)) return false;

            utc = parsed.Kind switch
            {
                DateTimeKind.Utc   => parsed,
                DateTimeKind.Local => parsed.ToUniversalTime(),
                // Журнал всегда пишет UTC (DateTime.UtcNow.ToString("O")) — запись без
                // смещения трактуем как UTC, а не как местное время.
                _                  => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            };
            return true;
        }
    }
}
