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
            // Коды APPINSTALLER_CLI_ERROR_* сверены с официальным списком winget-cli
            // (doc/windows/package-manager/winget/returnCodes.md). До 2026-09-23 часть
            // из них была перепутана: 0x8A150109 («перезагрузите для завершения», то есть
            // успех) выдавался за несовпадение хеша, а 0x8A15002C («upgrade --all
            // завершился с ошибками») — за успех с перезагрузкой.
            { 0, "Успешно." },
            { unchecked((int)0x8A150005), "Операция winget прервана сигналом завершения." },
            { unchecked((int)0x8A150011), "Хеш установщика не совпал с ожидаемым — повреждённая загрузка или изменённый пакет." },
            { unchecked((int)0x8A150014), "Пакет не найден в источнике или недоступен для этой системы." },
            { unchecked((int)0x8A150019), "Отказано в доступе — команда требует прав администратора." },
            { unchecked((int)0x8A15002B), "Обновление недоступно — версия в источнике не подходит для данной системы." },
            { unchecked((int)0x8A15002C), "Обновление части пакетов завершилось с ошибками." },
            { unchecked((int)0x8A150109), "Установлено, для завершения требуется перезагрузка." },
            { unchecked((int)0x8A15010A), "Установка не удалась: перезагрузите компьютер и попробуйте снова." },
            { unchecked((int)0x8A15010B), "Установлено, компьютер будет перезагружен для завершения." },
            { unchecked((int)0x80072EE2), "Ошибка сети — источник недоступен, попробуйте позже." },
            { unchecked((int)0x80072EFE), "Ошибка сети — соединение разорвано, попробуйте позже." },
        };

        /// <summary>
        /// Коды «установка прошла, но нужна перезагрузка»: 3010 (Windows Installer,
        /// ERROR_SUCCESS_REBOOT_REQUIRED) и собственные коды winget
        /// INSTALL_REBOOT_REQUIRED_TO_FINISH / INSTALL_REBOOT_INITIATED.
        /// </summary>
        public static bool IsSuccessWithReboot(int exitCode) =>
            exitCode == 3010 ||
            exitCode == unchecked((int)0x8A150109) ||
            exitCode == unchecked((int)0x8A15010B);

        public static string MapExitCode(int exitCode) =>
            KnownExitCodes.TryGetValue(exitCode, out var message)
                ? message
                : $"winget завершился с кодом {exitCode} (0x{exitCode:X8}). Подробности — в логе.";
    }
}
