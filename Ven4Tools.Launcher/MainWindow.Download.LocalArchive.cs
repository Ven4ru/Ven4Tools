using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Чем закончилась установка клиента из локального архива. Отказ из-за понижения
    /// версии выделен отдельно от прочих неудач: командной строке нужен свой код
    /// возврата, по которому скрипт отличит «архив старый» от «установка сломалась».
    /// </summary>
    internal enum LocalArchiveInstallStatus
    {
        Installed,
        Failed,
        DowngradeRefused,
    }

    /// <param name="Status">Исход установки.</param>
    /// <param name="Message">Пояснение для командной строки, если оно есть.</param>
    internal readonly record struct LocalArchiveInstallResult(
        LocalArchiveInstallStatus Status, string? Message = null)
    {
        public static LocalArchiveInstallResult Of(bool installed) =>
            new(installed ? LocalArchiveInstallStatus.Installed : LocalArchiveInstallStatus.Failed);
    }

    public partial class MainWindow
    {
        private void BtnInstallFromFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: установка из локального файла");
                return;
            }

            // Быстрая проверка ДО открытия диалога — чтобы не заставлять выбирать файл
            // впустую, когда лаунчер очевидно занят. Решение принимает не она, а
            // атомарный TryBeginOperation ниже: слот нельзя занимать на всё время
            // модального диалога (он может провисеть дольше бюджета операции).
            if (_operations.IsBusy)
            {
                AddLog("⏳ Уже идёт другая операция — установка из файла отложена до её завершения");
                System.Windows.MessageBox.Show(
                    $"Сейчас выполняется другая операция: {_operations.CurrentOperation ?? "загрузка или установка"}.\n\n" +
                    "Дождитесь её завершения и повторите.",
                    "Лаунчер занят", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Архив клиента Ven4Tools (*.zip)|*.zip",
                Title = "Выберите архив клиента"
            };
            if (dialog.ShowDialog() != true) return;

            // Диалог мог провисеть сколько угодно — за это время фоновая проверка
            // обновлений могла начать тихую установку. Занять слот и убедиться, что он
            // свободен, — теперь одно неделимое действие.
            var lease = TryBeginOperation("Установка клиента из файла", TimeSpan.FromMinutes(10));
            if (lease == null) return;

            _ = RunLocalArchiveInstallAsync(dialog.FileName, lease);
        }

        // Аренду держит вся операция целиком, поэтому освобождается она здесь, а не в
        // обработчике клика: установка запускается «в фоне» (fire-and-forget) и
        // переживает возврат из обработчика.
        private async Task RunLocalArchiveInstallAsync(string archivePath, OperationLease lease)
        {
            using (lease)
                await InstallFromLocalArchiveAsync(
                    archivePath, lease.Token, silent: false, LocalArchiveDowngradeMode.Ask);
        }

        // Точка входа headless-режима --install-from (CliInstallRunner). Слот нужен и
        // здесь: конструктор окна уже запустил фоновую проверку обновлений, и тихое
        // автообновление клиента без занятого слота пошло бы в ту же папку параллельно.
        //
        // Архив более старой версии здесь не ставится без ключа --allow-downgrade и
        // без вопросов: скрипту отвечать на диалог некому, а молча откатить клиента
        // на старую сборку — худший из исходов.
        internal async Task<LocalArchiveInstallResult> InstallFromLocalArchiveCliAsync(
            string archivePath, bool silent, bool allowDowngrade)
        {
            using var lease = TryBeginOperation(
                "Установка клиента из файла (командная строка)", Timeout.InfiniteTimeSpan, silent: true);
            if (lease == null)
                return new LocalArchiveInstallResult(
                    LocalArchiveInstallStatus.Failed, "лаунчер занят другой операцией");

            return await InstallFromLocalArchiveAsync(
                archivePath, lease.Token, silent,
                allowDowngrade ? LocalArchiveDowngradeMode.Allow : LocalArchiveDowngradeMode.Refuse);
        }

        internal async Task<LocalArchiveInstallResult> InstallFromLocalArchiveAsync(
            string archivePath, CancellationToken token, bool silent, LocalArchiveDowngradeMode downgradeMode)
        {
            Dispatcher.Invoke(() =>
            {
                progressDownload.Value = 0;
                txtDownloadStatus.Text = "Проверка подписи...";
                btnCancelDownload.Visibility = silent ? Visibility.Collapsed : Visibility.Visible;
                btnLaunchApp.IsEnabled = false;
                btnInstallFromFile.IsEnabled = false;
                // «Установить компоненты» тоже блокируем на время установки клиента:
                // её обработчик — такая же долгая операция, конкурирующая за тот же
                // прогресс и ту же кнопку «Отмена».
                btnInstallMissing.IsEnabled = false;
            });
            Dispatcher.Invoke(() => SetOperationStage(2)); // Проверка целостности

            try
            {
                AddLog($"📂 Установка из локального файла: {archivePath}");
                // FileShare.Read-хендл от проверки подписи до конца распаковки — та же
                // защита от подмены (TOCTOU), что у сетевого пути (DownloadResult): архив
                // выбран пользователем в папке, доступной на запись любому его процессу,
                // а между проверкой и распаковкой может висеть диалог «архивная версия»
                // или «клиент запущен». Без FileShare.Delete файл нельзя и переименовать.
                using var archiveGuard = new FileStream(
                    archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var cdnService = new CdnService();
                var result = await LocalArchiveVerifier.VerifyAsync(archivePath, cdnService, token);

                if (result.Outcome == LocalArchiveOutcome.Rejected)
                {
                    Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отклонено");
                    Dispatcher.Invoke(() => SetOperationStage(0));
                    AddLog($"⛔ {result.RejectionReason}");
                    if (!silent)
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            result.RejectionReason, "Установка отклонена",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                    return new LocalArchiveInstallResult(
                        LocalArchiveInstallStatus.Failed, result.RejectionReason);
                }

                AddLog(result.Outcome == LocalArchiveOutcome.Offline
                    ? $"✅ Офлайн-подпись подтверждена (версия {result.Version})"
                    : $"✅ Подтверждено по списку исторических версий (версия {result.Version})");

                // Понижение версии. Подпись говорит, что архив подлинный, но не что он
                // свежий: старая сборка подписана так же правильно, а исправлений после
                // выпуска не получает. Раньше такой архив ставился молча — в отличие от
                // сетевого пути, где откат отсекается (SignedArchiveFallbackPolicy).
                // Опубликованная версия — самая новая из известных: из манифеста,
                // полученного при этой проверке, и из последней загрузки списка версий
                // (в режиме командной строки окно не показывается и списка нет).
                var downgrade = ClientDowngradePolicy.Check(
                    result.Version,
                    ReadInstalledClientVersion(),
                    ClientDowngradePolicy.Newest(
                        result.PublishedClientVersion,
                        _cdnClientVersion,
                        _availableVersions.FirstOrDefault(v => v.IsLatest)?.Version));
                if (downgrade is { } older)
                {
                    // Спросить можно только в окне: тихому запуску отвечать некому.
                    if (downgradeMode == LocalArchiveDowngradeMode.Ask && silent)
                        downgradeMode = LocalArchiveDowngradeMode.Refuse;

                    if (downgradeMode == LocalArchiveDowngradeMode.Refuse)
                    {
                        string refusal = ClientDowngradePolicy.BuildRefusal(older);
                        Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отклонено");
                        Dispatcher.Invoke(() => SetOperationStage(0));
                        AddLog($"⛔ {refusal}");
                        return new LocalArchiveInstallResult(
                            LocalArchiveInstallStatus.DowngradeRefused, refusal);
                    }

                    AddLog($"⚠️ {older.Describe()}");
                    if (downgradeMode == LocalArchiveDowngradeMode.Allow)
                    {
                        AddLog($"⚠️ Понижение версии разрешено ключом {ClientDowngradePolicy.AllowDowngradeSwitch}");
                    }
                    else
                    {
                        // Кнопка по умолчанию — «Нет»: Enter по привычке не должен
                        // откатывать клиента на старую сборку.
                        var answer = Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            ClientDowngradePolicy.BuildQuestion(older), "Более старая версия",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No));
                        if (answer != MessageBoxResult.Yes)
                        {
                            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
                            Dispatcher.Invoke(() => SetOperationStage(0));
                            AddLog("⏹ Установка более старой версии отменена пользователем");
                            return LocalArchiveInstallResult.Of(false);
                        }
                        AddLog("⚠️ Установка более старой версии подтверждена пользователем");
                    }
                }

                if (result.Outcome == LocalArchiveOutcome.Historical)
                {
                    string warning =
                        $"Это архивная версия {result.Version} — подтверждена по списку ранее опубликованных " +
                        "версий, но не имеет встроенной подписи.\n\nРекомендуем скачать актуальную версию через " +
                        "обычную загрузку.\n\nВсё равно установить архивную версию?";
                    AddLog($"⚠️ Архивная версия {result.Version} без встроенной подписи, подтверждена по сети");

                    if (!silent)
                    {
                        var answer = Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            warning, "Архивная версия", MessageBoxButton.YesNo, MessageBoxImage.Warning));
                        if (answer != MessageBoxResult.Yes)
                        {
                            Dispatcher.Invoke(() => txtDownloadStatus.Text = "Отменено");
                            Dispatcher.Invoke(() => SetOperationStage(0));
                            AddLog("⏹ Установка архивной версии отменена пользователем");
                            return LocalArchiveInstallResult.Of(false);
                        }
                    }
                }

                bool installed = await ExtractAndInstallClientAsync(archivePath, result.Version ?? "?", token, silent);
                // Архив больше не нужен — не держим его занятым, пока висит сообщение ниже.
                archiveGuard.Dispose();
                if (installed && !silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Клиент {result.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information));
                return LocalArchiveInstallResult.Of(installed);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => { txtDownloadStatus.Text = "Отменено"; progressDownload.Value = 0; });
                Dispatcher.Invoke(() => SetOperationStage(0));
                AddLog("⏹ Установка из файла отменена");
                return LocalArchiveInstallResult.Of(false);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Ошибка");
                Dispatcher.Invoke(() => SetOperationStage(0));
                AddLog($"❌ Ошибка установки из файла: {ex.Message}");
                if (!silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));
                return new LocalArchiveInstallResult(LocalArchiveInstallStatus.Failed, ex.Message);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    btnCancelDownload.Visibility = Visibility.Collapsed;
                    btnCancelDownload.IsEnabled = true;
                    btnLaunchApp.IsEnabled = true;
                    btnInstallFromFile.IsEnabled = true;
                    btnInstallMissing.IsEnabled = true;
                });
            }
        }
    }
}
