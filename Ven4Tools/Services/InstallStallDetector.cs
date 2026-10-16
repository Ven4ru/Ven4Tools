using System;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Распознаёт зависшую установку по отсутствию признаков работы.
    /// Чистая логика без процессов и диска — тестируется отдельно (см. InstallStallDetectorTests).
    /// </summary>
    /// <remarks>
    /// Найдено на стенде 16.10.2026: одно зависшее скачивание winget (обрыв соединения,
    /// окно установщика, которого нет на экране) навсегда останавливало тихую
    /// установку всего набора — клиент ждал winget без срока. Простой общий тайм-аут
    /// не годится: Visual Studio честно ставится 20–40 минут. Поэтому признак зависания —
    /// «показатель активности» не меняется дольше <see cref="IdleLimit"/>, а общий
    /// предел <see cref="HardLimit"/> лишь страхует от бесконечного медленного цикла.
    /// </remarks>
    internal sealed class InstallStallDetector
    {
        public TimeSpan IdleLimit { get; }
        public TimeSpan HardLimit { get; }

        private readonly DateTime _startedUtc;
        private DateTime _lastChangeUtc;
        private long? _lastValue;

        public InstallStallDetector(DateTime startedUtc, TimeSpan idleLimit, TimeSpan hardLimit)
        {
            _startedUtc = startedUtc;
            _lastChangeUtc = startedUtc;
            IdleLimit = idleLimit;
            HardLimit = hardLimit;
        }

        /// <summary>
        /// Принимает очередной замер активности (сумма байтов ввода-вывода дерева процессов,
        /// размер папки загрузки и т. п.). Возвращает причину остановки или null, если ждать дальше.
        /// </summary>
        public string? Observe(DateTime nowUtc, long activity)
        {
            if (_lastValue != activity)
            {
                _lastValue = activity;
                _lastChangeUtc = nowUtc;
            }
            if (nowUtc - _startedUtc >= HardLimit)
                return $"установка идёт дольше {(int)HardLimit.TotalMinutes} мин";
            if (nowUtc - _lastChangeUtc >= IdleLimit)
                return $"{(int)IdleLimit.TotalMinutes} мин без признаков работы";
            return null;
        }
    }
}
