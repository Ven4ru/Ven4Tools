using System;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Состояние Smart App Control по реестру.</summary>
    internal enum SmartAppControlState
    {
        /// <summary>Ключа нет: Windows 10 или сборка Windows 11 без Smart App Control.</summary>
        NotPresent,
        /// <summary>Выключен (0).</summary>
        Off,
        /// <summary>Включён — блокирует неподписанные программы без репутации.</summary>
        On,
        /// <summary>Режим оценки: пока ничего не блокирует, Windows решает, включать ли.</summary>
        Evaluation,
        /// <summary>Значение есть, но незнакомое или не читается.</summary>
        Unknown
    }

    /// <summary>
    /// Л5 Smart App Control (SAC). Только ЧТЕНИЕ — ничего не выключает и не советует
    /// выключать «за пользователя»: Microsoft не даёт разрешить в SAC отдельную программу,
    /// а его выключение необратимо без переустановки на части сборок — это решение
    /// пользователя, лаунчер лишь честно объясняет причину блокировки.
    ///
    /// Вся трактовка значения реестра — в <see cref="Interpret"/> и константе
    /// <see cref="InterpretationVerified"/>: источники расходятся (Deskmodder: 1 = вкл,
    /// 2 = оценка; WiseChecker — наоборот). Пока трактовка не проверена на ВМ,
    /// пользовательские тексты для 1 и 2 одинаково нейтральные: «включён или работает в
    /// режиме оценки». ПРОВЕРИТЬ НА ВМ Windows 11 25H2: Безопасность Windows → Управление
    /// приложениями и браузером → Smart App Control — сверить «Вкл»/«Оценка» со значением
    /// VerifiedAndReputablePolicyState и выставить InterpretationVerified = true.
    /// </summary>
    internal static class SmartAppControl
    {
        private const string PolicyKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
        private const string PolicyValue = "VerifiedAndReputablePolicyState";

        /// <summary>
        /// Проверена ли трактовка 1/2 на живой системе. false — тексты нейтральные
        /// (см. <see cref="Describe"/>). ПРОВЕРИТЬ НА ВМ и переключить.
        /// </summary>
        internal const bool InterpretationVerified = false;

        // Ошибки Win32 при запуске процесса, которые означают «запрещено политикой».
        internal const int ErrorSystemIntegrityPolicyViolation = 4551; // SAC / WDAC (Code Integrity)
        internal const int ErrorAccessDisabledByPolicy = 1260;         // AppLocker / SRP / групповая политика

        /// <summary>
        /// Единственное место трактовки значения реестра. По Deskmodder: 0 — выкл,
        /// 1 — вкл, 2 — оценка (ПРОВЕРИТЬ НА ВМ). Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static SmartAppControlState Interpret(object? value)
        {
            if (value == null) return SmartAppControlState.NotPresent;
            if (value is not int v) return SmartAppControlState.Unknown;
            return v switch
            {
                0 => SmartAppControlState.Off,
                1 => SmartAppControlState.On,          // ПРОВЕРИТЬ НА ВМ (у WiseChecker — оценка)
                2 => SmartAppControlState.Evaluation,  // ПРОВЕРИТЬ НА ВМ (у WiseChecker — вкл)
                _ => SmartAppControlState.Unknown,
            };
        }

        /// <summary>Чтение реестра (HKLM, только чтение). Ошибка — Unknown.</summary>
        internal static SmartAppControlState Read()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
                return Interpret(key?.GetValue(PolicyValue));
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Smart App Control] Состояние не прочитано: {ex.GetType().Name}: {ex.Message}");
                return SmartAppControlState.Unknown;
            }
        }

        /// <summary>
        /// Может ли SAC сейчас блокировать запуск клиента (для предупреждения до установки).
        /// Пока трактовка не проверена, «оценку» считаем возможной блокировкой: лучше лишний
        /// раз предупредить, чем промолчать перед блокировкой.
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static bool MayBlock(SmartAppControlState state, bool interpretationVerified = InterpretationVerified) =>
            state == SmartAppControlState.On ||
            (!interpretationVerified && state == SmartAppControlState.Evaluation);

        /// <summary>Короткое описание состояния для людей. Чистая функция — покрыта unit-тестами.</summary>
        internal static string Describe(SmartAppControlState state, bool interpretationVerified = InterpretationVerified) => state switch
        {
            SmartAppControlState.NotPresent => "нет в этой версии Windows",
            SmartAppControlState.Off => "выключен",
            SmartAppControlState.On when interpretationVerified => "включён",
            SmartAppControlState.Evaluation when interpretationVerified => "в режиме оценки",
            SmartAppControlState.On or SmartAppControlState.Evaluation => "включён или работает в режиме оценки",
            _ => "нет данных",
        };

        /// <summary>Текст предупреждения перед установкой клиента. Чистая функция.</summary>
        internal static string BuildPreInstallWarning(SmartAppControlState state) =>
            $"В Windows обнаружен Smart App Control ({Describe(state)}).\n\n" +
            "Он запрещает запуск программ без цифровой подписи и «репутации» у Microsoft, " +
            "и кнопки «всё равно запустить» у него нет. Клиент Ven4Tools пока такой репутации не имеет, " +
            "поэтому после установки Windows может не дать его запустить.\n\n" +
            "Лаунчер ничего не меняет в настройках Windows. Если запуск будет заблокирован, " +
            "лаунчер покажет причину. Выключить Smart App Control можно только вручную: " +
            "Безопасность Windows → Управление приложениями и браузером → Smart App Control " +
            "(обратно включить его, возможно, можно будет только переустановкой Windows).\n\n" +
            "Продолжить установку?";

        /// <summary>
        /// Запрет запуска политикой целостности кода (SAC/WDAC) или политикой
        /// AppLocker/SRP. Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static bool IsPolicyBlock(int? win32Error) =>
            win32Error is ErrorSystemIntegrityPolicyViolation or ErrorAccessDisabledByPolicy;

        /// <summary>Объяснение блокировки запуска. Чистая функция — покрыта unit-тестами.</summary>
        internal static string BuildLaunchBlockedText(int win32Error, SmartAppControlState state, bool? eventConfirmed)
        {
            string head = win32Error == ErrorAccessDisabledByPolicy
                ? "Windows запретила запуск клиента политикой (код 1260): это AppLocker, политика ограниченного использования программ или настройка администратора компьютера."
                : "Windows запретила запуск клиента политикой целостности кода (код 4551).";

            string reason;
            if (win32Error == ErrorAccessDisabledByPolicy)
                reason = "Разрешить запуск может только администратор этого компьютера.";
            else if (state is SmartAppControlState.On or SmartAppControlState.Evaluation)
                reason = $"Smart App Control в этой системе {Describe(state)}. Он блокирует программы без цифровой подписи и репутации у Microsoft — " +
                         "клиент Ven4Tools пока такой репутации не имеет. Разрешить в нём одну программу Windows не позволяет.";
            else
                reason = "Похоже на политику Windows Defender Application Control (её задаёт администратор) или на Smart App Control.";

            string evidence = eventConfirmed switch
            {
                true => "Запись о блокировке есть в журнале Windows (CodeIntegrity, событие 3077).",
                false => "В журнале CodeIntegrity свежей записи о блокировке не найдено.",
                null => "Журнал CodeIntegrity прочитать не удалось — нет данных.",
            };

            return head + "\n\n" + reason + "\n\n" + evidence + "\n\n" +
                   "Лаунчер ничего не выключает за вас. Варианты: дождаться версии клиента с подписью и репутацией, " +
                   "или самостоятельно выключить Smart App Control (Безопасность Windows → Управление приложениями и браузером), " +
                   "понимая, что обратно его, возможно, получится включить только переустановкой Windows.";
        }

        /// <summary>
        /// Есть ли за последние минуты событие 3077 (блокировка, режим принуждения) в журнале
        /// Microsoft-Windows-CodeIntegrity/Operational с именем нашего файла. null — журнал
        /// не читается (нет прав/нет журнала): «нет данных».
        /// </summary>
        internal static bool? FindRecentBlockEvent(string fileName, TimeSpan window)
        {
            try
            {
                long ms = (long)window.TotalMilliseconds;
                var query = new EventLogQuery("Microsoft-Windows-CodeIntegrity/Operational", PathType.LogName,
                    $"*[System[(EventID=3077 or EventID=3033) and TimeCreated[timediff(@SystemTime) <= {ms}]]]")
                {
                    ReverseDirection = true
                };
                using var reader = new EventLogReader(query);
                for (int i = 0; i < 50 && reader.ReadEvent() is { } record; i++)
                {
                    using (record)
                    {
                        string? text = null;
                        try { text = record.FormatDescription(); } catch { /* описание может не форматироваться */ }
                        if (string.IsNullOrEmpty(text))
                        {
                            try { text = record.ToXml(); } catch { /* XML события недоступен */ }
                        }
                        if (text != null && text.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Smart App Control] Журнал CodeIntegrity не прочитан: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
