using System;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Результат шага «точка восстановления перед массовой операцией».
    /// </summary>
    public enum RestorePointOutcome
    {
        /// <summary>Точка восстановления успешно создана.</summary>
        Created,

        /// <summary>Пользователь отказался от создания точки либо её не удалось
        /// создать — операцию можно продолжать (преобладающее поведение в коде).</summary>
        Skipped,

        /// <summary>Пользователь отменил всю операцию.</summary>
        Cancelled
    }

    /// <summary>
    /// Общие UI-гарды для вкладок и вью-моделей: единая проверка занятости
    /// установки и единый диалог «создать точку восстановления». Раньше оба блока
    /// дублировались в InstalledTab/HistoryTab/CatalogViewModel/DebloaterTab/SystemTab
    /// с расходящимися формулировками и обработкой отказа.
    /// </summary>
    public static class UiGuards
    {
        /// <summary>
        /// Если установка сейчас занята — показывает предупреждение и возвращает true
        /// (вызывающему коду следует прервать операцию: <c>if (UiGuards.WarnIfInstallBusy()) return;</c>).
        /// </summary>
        public static bool WarnIfInstallBusy()
        {
            if (!InstallationService.IsBusy) return false;

            MessageBox.Show(
                "Дождитесь завершения текущей установки, затем повторите попытку.",
                "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        /// <summary>
        /// Показывает диалог «создать точку восстановления перед …?» (Да/Нет/Отмена),
        /// при выборе «Да» создаёт точку через <see cref="SystemRestoreService"/> и
        /// логирует результат. Возвращает исход операции.
        /// <para>Обработка отказа единая для всех вызовов: если пользователь выбрал
        /// «Нет» либо точку создать не удалось — возвращается <see cref="RestorePointOutcome.Skipped"/>,
        /// и вызывающий код продолжает операцию (преобладающее поведение до унификации).</para>
        /// </summary>
        /// <param name="question">Текст вопроса (специфичен для операции).</param>
        /// <param name="restoreDescription">Описание точки восстановления.</param>
        /// <param name="log">Необязательный логгер статуса. По умолчанию — только файловый
        /// лог (<see cref="AppLogger.Write(string)"/>); вкладка каталога передаёт свой
        /// логгер, чтобы статус дублировался в панель лога вкладки, как было раньше.</param>
        public static async Task<RestorePointOutcome> ConfirmAndCreateRestorePointAsync(
            string question, string restoreDescription, Action<string>? log = null)
        {
            var answer = MessageBox.Show(
                question, "Точка восстановления",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return RestorePointOutcome.Cancelled;
            if (answer != MessageBoxResult.Yes) return RestorePointOutcome.Skipped;

            log ??= AppLogger.Write;
            log("🛡️ Создаю точку восстановления...");
            bool ok = await SystemRestoreService.CreateRestorePointAsync(restoreDescription);
            log(ok
                ? "✅ Точка восстановления создана"
                : "⚠️ Точка восстановления не создана (можно продолжать)");

            return ok ? RestorePointOutcome.Created : RestorePointOutcome.Skipped;
        }

        /// <summary>
        /// Единый диалог согласия (Да/Нет) на автоматическую установку пакетного
        /// менеджера (winget/Chocolatey), которого ещё нет в системе. Возвращает
        /// true, если пользователь разрешил установку. Раньше этот диалог дублировался
        /// в CatalogViewModel/MainWindow/HistoryTab с расходящимися формулировками.
        /// <para>Показ маршалится в UI-поток, поэтому метод безопасно вызывать из
        /// фонового потока установки (сигнатура совместима с параметром
        /// <c>confirmPmInstall</c> у <see cref="InstallationService.InstallAppAsync"/>).</para>
        /// </summary>
        public static async Task<bool> ConfirmPackageManagerInstallAsync(string pmName)
        {
            bool Ask() => MessageBox.Show(
                $"Для установки приложения требуется {pmName}, который сейчас не установлен.\n\n" +
                $"Разрешить автоматическую установку {pmName}?",
                $"Установка {pmName}",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return Ask();
            return await dispatcher.InvokeAsync(Ask);
        }
    }
}
