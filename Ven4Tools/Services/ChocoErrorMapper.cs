using System.Collections.Generic;

namespace Ven4Tools.Services
{
    // Chocolatey либо возвращает свои коды (0/1/2), либо пробрасывает код
    // выхода установщика пакета внутри — общий диапазон Windows Installer
    // с WingetErrorMapper вынесен в MsiExitCodes, коды Chocolatey (0/1/2)
    // добавляются здесь же и в WingetErrorMapper не имели бы смысла.
    public static class ChocoErrorMapper
    {
        private static readonly Dictionary<int, string> KnownExitCodes = new(MsiExitCodes.Known)
        {
            { 0, "Успешно." },
            { 1, "Chocolatey сообщил об ошибке при установке пакета." },
            { 2, "Установка завершена с предупреждениями." },
            // -1 — синтетический код без записи здесь: он означает "реального кода
            // выхода нет" (невалидный ID, choco не найден, таймаут, исключение) —
            // разные причины, единого текста для них нет. Вызывающий код
            // (InstallFromChocoAsync) перехватывает -1 ДО вызова MapExitCode именно
            // по этой причине; если он всё же сюда попадёт — честный фолбэк с самим
            // кодом лучше, чем неверное утверждение про конкретную причину.
        };

        public static string MapExitCode(int exitCode) =>
            KnownExitCodes.TryGetValue(exitCode, out var message)
                ? message
                : $"choco завершился с кодом {exitCode}. Подробности — в логе установки.";
    }
}
