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
        /// <summary>
        /// APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED: пакет (например, Battle.net)
        /// ставится только с явным --location. Пойман на стенде 2026-10-16.
        /// </summary>
        public const int InstallLocationRequired = unchecked((int)0x8A15005F);

        /// <summary>
        /// Не код winget, а признак клиента: процесс winget завис и прерван сторожем
        /// (InstallationService.WaitForWingetAsync). -1 занят под «winget не запускался».
        /// </summary>
        public const int StalledByClient = -2;

        /// <summary>
        /// APPINSTALLER_CLI_ERROR_ADMIN_CONTEXT_ACTION_PROHIBITED: winget не удаляет и не
        /// меняет программы из профиля пользователя, если запущен от имени администратора.
        /// </summary>
        public const int AdminContextProhibited = unchecked((int)0x8A15007D);

        /// <summary>
        /// APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER: ни один установщик пакета не подходит
        /// под условия — в том числе под --scope machine, если у пакета есть только установщик
        /// для профиля пользователя.
        /// </summary>
        public const int NoApplicableInstaller = unchecked((int)0x8A150010);

        /// <summary>
        /// APPINSTALLER_CLI_ERROR_INSTALLER_PROHIBITS_ELEVATION: установщик нельзя запускать
        /// от имени администратора, а клиент работает именно так.
        /// </summary>
        public const int InstallerProhibitsElevation = unchecked((int)0x8A150056);

        private static readonly Dictionary<int, string> KnownExitCodes = new(MsiExitCodes.Known)
        {
            // Коды APPINSTALLER_CLI_ERROR_* сверены с официальным списком winget-cli
            // (doc/windows/package-manager/winget/returnCodes.md). До 2026-09-23 часть
            // из них была перепутана: 0x8A150109 («перезагрузите для завершения», то есть
            // успех) выдавался за несовпадение хеша, а 0x8A15002C («upgrade --all
            // завершился с ошибками») — за успех с перезагрузкой.
            { 0, "Успешно." },
            { unchecked((int)0x8A150005), "Операция winget прервана сигналом завершения." },
            { NoApplicableInstaller, "Ни один установщик пакета не подходит для этой системы." },
            { unchecked((int)0x8A150011), "Хеш установщика не совпал с ожидаемым — повреждённая загрузка или изменённый пакет." },
            { unchecked((int)0x8A150014), "Пакет не найден в источнике или недоступен для этой системы." },
            { unchecked((int)0x8A150019), "Отказано в доступе — команда требует прав администратора." },
            { unchecked((int)0x8A15002B), "Обновление недоступно — версия в источнике не подходит для данной системы." },
            { unchecked((int)0x8A15002C), "Обновление части пакетов завершилось с ошибками." },
            { unchecked((int)0x8A15004F), "Установленная версия не старее предлагаемой." },
            { InstallLocationRequired, "Пакет требует явно указать папку установки." },
            { AdminContextProhibited, "Программа установлена в профиль пользователя — winget не меняет её от имени администратора." },
            { InstallerProhibitsElevation, "Установщик нельзя запускать от имени администратора." },
            { unchecked((int)0x8A150101), "Программа сейчас запущена — закройте её и повторите установку." },
            { unchecked((int)0x8A150103), "Файлы программы заняты — закройте её и повторите установку." },
            { unchecked((int)0x8A150104), "Не хватает компонента, без которого пакет не ставится." },
            { unchecked((int)0x8A150105), "На диске не хватает места." },
            { unchecked((int)0x8A150106), "Не хватает памяти — закройте другие программы и повторите." },
            { unchecked((int)0x8A150107), "Установщику нужен интернет." },
            { unchecked((int)0x8A15010C), "Установка отменена." },
            { unchecked((int)0x8A15010D), "Другая версия программы уже установлена." },
            { unchecked((int)0x8A15010E), "Уже установлена более новая версия программы." },
            { unchecked((int)0x8A150110), "Не удалось установить компоненты, нужные пакету." },
            { unchecked((int)0x8A150111), "Программа занята другим приложением — закройте его и повторите установку." },
            { StalledByClient, "winget завис (скачивание или установщик не подавали признаков работы) и прерван клиентом." },
            // INSTALL_INSTALL_IN_PROGRESS: winget так передаёт 1618 от MSI-установщика.
            // Текст тот же, что у 1618, — по нему набор находит программы для повтора
            // (InstallerLockGuard.IsMsiBusyFailure).
            { unchecked((int)0x8A150102), MsiExitCodes.Known[1618] },
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

        /// <summary>
        /// Что делать установке после неудачи winget. Раньше любая неудача вела к choco и
        /// прямой ссылке — и там пользователь минутами ждал ту же самую проблему: запущенную
        /// программу, переполненный диск, отменённую установку.
        /// </summary>
        public static WingetFailureAction ClassifyInstallFailure(int exitCode) => exitCode switch
        {
            // Уже стоит эта или более новая версия — ставить нечего.
            unchecked((int)0x8A15010D) or unchecked((int)0x8A15010E) or unchecked((int)0x8A15004F)
                => WingetFailureAction.AlreadyInstalled,
            // Программа или её файлы заняты, нет места, установку отменили —
            // у других источников будет та же причина.
            unchecked((int)0x8A150101) or unchecked((int)0x8A150103) or unchecked((int)0x8A150111) or
            unchecked((int)0x8A150105) or unchecked((int)0x8A15010C)
                => WingetFailureAction.Stop,
            _ => WingetFailureAction.NextSource
        };

        public static string MapExitCode(int exitCode) =>
            KnownExitCodes.TryGetValue(exitCode, out var message)
                ? message
                : $"winget завершился с кодом {exitCode} (0x{exitCode:X8}). Подробности — в логе.";
    }

    public enum WingetFailureAction
    {
        /// <summary>Пробовать следующий источник (choco, прямая ссылка).</summary>
        NextSource,
        /// <summary>Остановить цепочку: другие источники упрутся в ту же причину.</summary>
        Stop,
        /// <summary>Программа уже установлена — считать установку состоявшейся.</summary>
        AlreadyInstalled
    }
}
