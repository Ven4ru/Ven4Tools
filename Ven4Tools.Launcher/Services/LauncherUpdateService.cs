// Services/LauncherUpdateService.cs
using System;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Обновление установленного лаунчера (схема 2.1: только установщик) — тонкий
    /// фасад над двумя независимыми половинами процесса:
    ///   <see cref="LauncherUpdateChecker"/>  — есть ли версия новее и откуда её брать;
    ///   <see cref="LauncherUpdateInstaller"/> — скачать установщик и запустить его.
    /// Пути установки и версия текущего процесса — в <see cref="LauncherInstallation"/>.
    ///
    /// Лаунчер ставится установщиком Ven4Tools.Setup-X.Y.Z.exe в
    /// %LOCALAPPDATA%\Ven4Tools\Launcher\ и регистрируется в «Программы и
    /// компоненты». Самообновление работает через тот же установщик:
    ///   1. Setup-X.Y.Z.exe скачивается в уникальную папку %TEMP%\ven4tools_setup_&lt;guid&gt;
    ///      с обязательной проверкой SHA256 (из version.json CDN) и хоста после редиректа;
    ///   2. Установщик запускается с флагами тихого самообновления
    ///      (/S /UPDATE /WAITPID=&lt;pid&gt; /RELAUNCH — см. installer\Ven4Tools.Setup.nsi):
    ///      он дожидается завершения текущего процесса, делает бэкап exe,
    ///      ставит новую версию, проверяет её и перезапускает лаунчер
    ///      (при неудаче откатывает бэкап и запускает старую версию);
    ///   3. Текущий процесс завершается (это делает вызывающий код).
    ///
    /// Если лаунчер запущен НЕ из папки установки (например, из Downloads),
    /// OfferInstallationAsync() предлагает скачать и запустить установщик.
    /// </summary>
    public class LauncherUpdateService
    {
        private readonly LauncherUpdateChecker _checker;
        private readonly LauncherUpdateInstaller _installer;

        public LauncherUpdateService(Action<string>? log = null, DownloadSource preference = DownloadSource.Auto)
        {
            _checker = new LauncherUpdateChecker(log);
            _installer = new LauncherUpdateInstaller(_checker, log, preference);
        }

        /// <summary>
        /// Проверка обновления лаунчера. CDN version.json — основной источник
        /// обнаружения версии (симметрично клиенту), GitHub Releases — резерв.
        /// Возвращает null при сетевой ошибке (для вызывающего кода это «обновлений нет»).
        /// </summary>
        public Task<UpdateInfo?> CheckForUpdateAsync() => _checker.CheckForUpdateAsync();

        /// <summary>
        /// То же, но с явной текущей версией (для фоновой проверки, где версия
        /// передаётся снаружи). "0.0.0" — «любая доступная версия считается новее».
        /// </summary>
        public Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion) =>
            _checker.CheckForUpdateAsync(currentVersion);

        /// <summary>
        /// Скачивает установщик и запускает его в режиме тихого самообновления.
        /// При результате true вызывающий код ОБЯЗАН завершить приложение —
        /// установщик ждёт завершения процесса и только потом меняет файлы.
        /// </summary>
        /// <param name="updateInfo">Готовая информация об обновлении; если null — проверяется заново.</param>
        public Task<bool> DownloadAndRunSetupUpdateAsync(UpdateInfo? updateInfo = null) =>
            _installer.DownloadAndRunSetupUpdateAsync(updateInfo);

        /// <summary>
        /// Если лаунчер запущен не из папки установки — предлагает скачать и
        /// запустить установщик последней версии. true — установщик запущен,
        /// вызывающий код должен завершить приложение.
        /// </summary>
        public Task<bool> OfferInstallationAsync() => _installer.OfferInstallationAsync();
    }
}
