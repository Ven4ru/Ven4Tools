using System.Collections.Generic;

namespace Ven4Tools.Services
{
    // Расшифровка числового кода выхода winget в читаемое сообщение. Коды
    // собраны из практики этого проекта (InstalledTab.BulkOps.cs,
    // AppUninstallService.cs) плюс типовые коды Windows Installer, которые
    // winget пробрасывает как есть, если пакет внутри — MSI (см. комментарий
    // про ошибку 1618 и общий семафор установки в InstallationService.cs).
    public static class WingetErrorMapper
    {
        private static readonly Dictionary<int, string> KnownExitCodes = new(MsiExitCodes.Known)
        {
            { 0, "Успешно." },
            { unchecked((int)0x8A15002C), "Установлено, требуется перезагрузка." },
            { unchecked((int)0x8A15002B), "Обновление недоступно — версия в источнике не подходит для данной системы." },
            { unchecked((int)0x8A150014), "Пакет не найден в источнике или недоступен для этой системы." },
            { unchecked((int)0x8A150109), "Хеш установщика не совпал с ожидаемым — повреждённая загрузка или изменённый пакет." },
            { unchecked((int)0x8A150005), "Отказано в доступе — установка требует прав администратора." },
            { unchecked((int)0x80072EE2), "Ошибка сети — источник недоступен, попробуйте позже." },
            { unchecked((int)0x80072EFE), "Ошибка сети — соединение разорвано, попробуйте позже." },
        };

        public static string MapExitCode(int exitCode) =>
            KnownExitCodes.TryGetValue(exitCode, out var message)
                ? message
                : $"winget завершился с кодом {exitCode} (0x{exitCode:X8}). Подробности — в логе.";
    }
}
