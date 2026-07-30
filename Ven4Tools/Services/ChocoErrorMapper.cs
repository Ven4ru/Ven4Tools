using System.Collections.Generic;

namespace Ven4Tools.Services
{
    // Chocolatey либо возвращает свои коды (0/1/2), либо пробрасывает код
    // выхода установщика пакета внутри (тот же диапазон Windows Installer,
    // что и у winget) — отдельная таблица от WingetErrorMapper, коды
    // Chocolatey (1/2) там не имели бы смысла.
    public static class ChocoErrorMapper
    {
        private static readonly Dictionary<int, string> KnownExitCodes = new()
        {
            { 0, "Успешно." },
            { 1, "Chocolatey сообщил об ошибке при установке пакета." },
            { 2, "Установка завершена с предупреждениями." },
            { -1, "Chocolatey не ответил вовремя — установка прервана по таймауту." },
            { 1602, "Установка отменена в диалоге установщика." },
            { 1603, "Установщик пакета завершился с фатальной ошибкой." },
            { 1618, "Другая установка уже выполняется в системе — повторите позже." },
            { 1641, "Установлено, инициирована перезагрузка." },
            { 3010, "Установлено, требуется перезагрузка." },
        };

        public static string MapExitCode(int exitCode) =>
            KnownExitCodes.TryGetValue(exitCode, out var message)
                ? message
                : $"choco завершился с кодом {exitCode}. Подробности — в логе установки.";
    }
}
