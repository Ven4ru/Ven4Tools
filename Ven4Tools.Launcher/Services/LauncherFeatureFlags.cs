using System;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Действующие значения флагов поведения из подписанного version.json
    /// (<see cref="LauncherFlags"/>). Значение берётся из последнего манифеста, чья
    /// подпись подтверждена, а между запусками хранится в настройках лаунчера —
    /// иначе выключатель не действовал бы ровно тогда, когда CDN в этот запуск
    /// недоступен (а загрузка идёт с GitHub или зеркала).
    ///
    /// Манифест не получен ни разу и в настройках ничего нет — значения по умолчанию.
    /// </summary>
    internal static class LauncherFeatureFlags
    {
        private static volatile bool _stallDetection = true;

        /// <summary>
        /// Детектор «зависшей» загрузки и докачка с того же байта (см. FallbackDownloader).
        /// По умолчанию включён.
        /// </summary>
        public static bool StallDetectionEnabled => _stallDetection;

        /// <summary>
        /// Значение поменялось по свежему подписанному манифесту — владелец настроек
        /// сохраняет его. Вызывается с потока, на котором пришёл манифест (не UI).
        /// </summary>
        public static event Action? Changed;

        /// <summary>Значение из настроек при старте (null — не сохранялось).</summary>
        public static void Seed(bool? stallDetection) => _stallDetection = stallDetection ?? true;

        /// <summary>
        /// Применить флаги из манифеста, подпись которого уже подтверждена. Отсутствие
        /// объекта или поля — значение по умолчанию, а не «оставить прежнее»: так
        /// снятие флага из манифеста возвращает поведение по умолчанию.
        /// </summary>
        public static void Apply(LauncherFlags? flags)
        {
            bool stall = flags?.StallDetection ?? true;
            if (stall == _stallDetection) return;

            _stallDetection = stall;
            LauncherLog.Write(stall
                ? "🚩 Флаг манифеста: детектор зависшей загрузки включён"
                : "🚩 Флаг манифеста: детектор зависшей загрузки выключен");
            try { Changed?.Invoke(); } catch { /* сохранение не должно ронять проверку версии */ }
        }
    }
}
