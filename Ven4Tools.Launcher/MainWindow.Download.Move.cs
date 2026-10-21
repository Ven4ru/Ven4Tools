using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        /// <summary>
        /// «Перенести клиент» (окно настроек): перенос уже установленного клиента в
        /// другую папку — по умолчанию в %LocalAppData%\Programs\Ven4Tools.
        ///
        /// Порядок выбран так, чтобы обрыв в любой точке оставлял рабочий клиент:
        /// 1) журнал переноса (что откуда куда) — до первого изменения на диске;
        /// 2) манифест старой папки (размер + SHA256 каждого файла);
        /// 3) копия во временную папку рядом с новым местом и перенос её на место;
        /// 4) сверка новой папки с манифестом — расхождение отменяет перенос;
        /// 5) смена указателя (ClientPath в настройках) — с этого момента клиент
        ///    запускается из новой папки;
        /// 6) ярлыки на новый путь;
        /// 7) удаление старой папки — только после всего перечисленного.
        /// До шага 5 указатель смотрит на старую папку, и при следующем старте журнал
        /// велит убрать недоделанную копию; после шага 5 — доудалить старую.
        /// Без нажатия кнопки ничего не переносится.
        /// </summary>
        /// <summary>Папка установленного клиента — для окна настроек.</summary>
        internal string CurrentClientPath => _clientPath;

        internal async Task<bool> MoveClientAsync()
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: перенос клиента");
                return false;
            }

            using var lease = TryBeginOperation("Перенос клиента", Timeout.InfiniteTimeSpan);
            if (lease == null) return false;

            string oldClientPath = ClientFolderMover.Normalize(_clientPath);
            if (!File.Exists(Path.Combine(oldClientPath, LauncherPaths.ClientExeName)))
            {
                MessageBox.Show(
                    "Клиент не установлен — переносить нечего.\n\nПапку для новой установки можно выбрать кнопкой «Изменить…» в карточке «Папка установки».",
                    "Перенос клиента", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            string? newRoot = ChooseMoveDestination();
            if (newRoot == null) return false;
            string newClientPath = ClientFolderMover.Normalize(Path.Combine(newRoot, LauncherPaths.ClientFolderName));

            string? refusal = ClientFolderMover.ValidateTarget(oldClientPath, newClientPath, _dataFolderPath);
            if (refusal != null)
            {
                AddLog($"⛔ Перенос клиента отменён: {refusal} ({newClientPath})");
                MessageBox.Show($"Перенести клиент в\n{newClientPath}\nнельзя: {refusal}.",
                    "Перенос клиента", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Запущенный клиент держит свои файлы, а копия запущенной программы может
            // оказаться несогласованной.
            if (IsClientRunning())
            {
                var answer = MessageBox.Show(
                    "Ven4Tools сейчас запущен.\n\nЗакрыть клиент, чтобы перенести его?",
                    "Клиент запущен", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return false;
                if (!await TryCloseRunningClientAsync())
                {
                    AddLog("⚠️ Клиент не закрылся — перенос отменён");
                    MessageBox.Show("Не удалось закрыть клиент. Закройте его вручную и повторите перенос.",
                        "Клиент не закрылся", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            string stagingPath = ClientFolderMover.StagingPathFor(newClientPath);
            string journalPath = LauncherPaths.ClientMoveJournalPath;
            bool pointerSwitched = false;
            bool newCopyCommitted = false;

            using var keepAwake = SleepBlocker.Acquire("перенос клиента");
            SetMoveUiBusy(true);
            try
            {
                AddLog($"📦 Перенос клиента: {oldClientPath} → {newClientPath}");
                new ClientMoveJournal
                {
                    OldClientPath = oldClientPath,
                    NewClientPath = newClientPath,
                    StagingPath = stagingPath,
                }.Save(journalPath);

                SetMoveStatus("Перенос: проверка файлов...");
                var manifest = await Task.Run(() => ClientFolderManifest.CaptureAsync(oldClientPath, lease.Token));
                AddLog($"🔒 Манифест переноса: файлов {manifest.FileCount}, {manifest.TotalBytes / (1024 * 1024)} МБ");

                // Места должно хватить с запасом: иначе копия оборвётся на середине.
                string? refusalBySpace = CheckFreeSpace(newClientPath, manifest.TotalBytes);
                if (refusalBySpace != null) throw new IOException(refusalBySpace);

                SetMoveStatus("Перенос: копирование...");
                long total = Math.Max(1, manifest.TotalBytes);
                await Task.Run(() => ClientFolderMover.CopyTreeAsync(oldClientPath, stagingPath, copied =>
                {
                    int percent = (int)(copied * 100 / total);
                    Dispatcher.BeginInvoke(() => progressDownload.Value = percent);
                }, lease.Token));

                // Пустую целевую папку (её разрешает ValidateTarget) убираем, чтобы
                // перенести копию на её место одним переименованием.
                if (Directory.Exists(newClientPath)) Directory.Delete(newClientPath);
                Directory.Move(stagingPath, newClientPath);
                newCopyCommitted = true;

                SetMoveStatus("Перенос: проверка копии...");
                string? difference = await Task.Run(() => manifest.FindDifferenceAsync(newClientPath, lease.Token));
                if (difference != null)
                    throw new IOException($"копия клиента не совпала с оригиналом ({difference})");
                AddLog("✅ Копия клиента совпала с манифестом");

                // Смена указателя. Отмена дальше не действует: перенос либо доделывается,
                // либо (если указатель не записался) откатывается.
                string previousInstallPath = _installPath;
                _installPath = Path.GetDirectoryName(newClientPath)!;
                _clientPath = newClientPath;
                if (!TrySaveSettings())
                {
                    _installPath = previousInstallPath;
                    _clientPath = oldClientPath;
                    throw new IOException("не удалось сохранить новую папку в настройках лаунчера");
                }
                pointerSwitched = true;
                txtInstallPath.Text = _clientPath;
                AddLog($"📁 Клиент теперь запускается из: {_clientPath}");

                SetMoveStatus("Перенос: ярлыки...");
                var shortcuts = await Task.Run(() =>
                    ClientShortcutRetargeter.Retarget(oldClientPath, newClientPath, AddLog));
                if (shortcuts.Updated > 0)
                    AddLog($"🔗 Ярлыков клиента обновлено: {shortcuts.Updated}");
                foreach (string failed in shortcuts.Failed)
                    AddLog($"⚠️ Ярлык не обновлён (нужны права администратора или он занят): {failed}");

                SetMoveStatus("Перенос: удаление старой папки...");
                bool oldRemoved = await RemoveOldClientCopyAsync(oldClientPath, newClientPath);
                if (oldRemoved)
                {
                    ClientMoveJournal.Delete(journalPath);
                }
                else
                {
                    // Журнал остаётся: следующий запуск попробует удалить старую копию снова.
                    AddLog($"⚠️ Старая папка клиента не удалена (файлы заняты?) — лаунчер попробует при следующем запуске: {oldClientPath}");
                }

                txtDownloadStatus.Text = "Готово";
                progressDownload.Value = 100;
                CheckExistingClient(quiet: true);
                MessageBox.Show(
                    $"Клиент перенесён в:\n{newClientPath}" +
                    (oldRemoved ? "" : $"\n\nСтарую папку удалить не удалось — лаунчер попробует ещё раз при следующем запуске:\n{oldClientPath}"),
                    "Перенос клиента", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                bool cancelled = ex is OperationCanceledException;
                AddLog(cancelled
                    ? "⏹ Перенос клиента отменён"
                    : $"❌ Перенос клиента не выполнен: {ex.Message}");

                if (pointerSwitched)
                {
                    // Указатель уже на новой проверенной папке — клиент работоспособен.
                    // Старая копия осталась; журнал доделает её удаление при старте.
                    return true;
                }

                // До смены указателя: убираем всё, что успели создать, клиент остаётся
                // в старой папке. Не удалось убрать — журнал доделает при старте.
                bool cleaned = await ClientFolderMover.TryDeleteDirectoryAsync(stagingPath);
                if (newCopyCommitted)
                    cleaned &= await ClientFolderMover.TryDeleteDirectoryAsync(newClientPath);
                if (cleaned) ClientMoveJournal.Delete(journalPath);

                if (!cancelled)
                    MessageBox.Show($"Не удалось перенести клиент: {ex.Message}\n\nКлиент остаётся на прежнем месте:\n{oldClientPath}",
                        "Перенос клиента", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                SetMoveUiBusy(false);
                // Сравнение с запомненным текстом, а не с «Перенос…»: в английском интерфейсе
                // в строке состояния уже перевод.
                if (_moveStatus != null && txtDownloadStatus.Text == _moveStatus)
                    txtDownloadStatus.Text = "";
                _moveStatus = null;
                SetOperationStage(0);
            }
        }

        // Куда переносить: рекомендуемая папка, своя или никуда.
        private string? ChooseMoveDestination()
        {
            string safeRoot = LauncherPaths.DefaultInstallRoot;
            var answer = MessageBox.Show(
                $"Перенести клиент в рекомендуемую папку?\n{safeRoot}\n\n" +
                "«Да» — в рекомендуемую папку.\n" +
                "«Нет» — выбрать другую папку.\n" +
                "«Отмена» — не переносить.\n\n" +
                "Настройки и данные клиента не затрагиваются: они хранятся отдельно.",
                "Перенос клиента", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Yes) return safeRoot;
            if (answer != MessageBoxResult.No) return null;

            using var dialog = new FolderBrowserDialog
            {
                Description = Tr("Выберите папку, куда перенести Ven4Tools"),
                ShowNewFolderButton = true,
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            return ConfirmInstallLocation(dialog.SelectedPath);
        }

        private static string? CheckFreeSpace(string targetClientPath, long requiredBytes)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(targetClientPath));
                if (string.IsNullOrEmpty(root)) return null;
                var drive = new DriveInfo(root);
                // Запас 10%, но не меньше 100 МБ: временные файлы антивируса, журнал NTFS.
                long needed = requiredBytes + Math.Max(requiredBytes / 10, 100L * 1024 * 1024);
                if (drive.AvailableFreeSpace < needed)
                    return $"на диске {root} недостаточно места (нужно около {needed / (1024 * 1024)} МБ)";
            }
            catch
            {
                // Нет данных о месте (сетевой путь и т.п.) — не мешаем, копирование
                // само сообщит о нехватке, а откат уберёт недоделанную копию.
            }
            return null;
        }

        // Удаление старой копии — только если новая на месте и старая действительно
        // выглядит как папка клиента: путь к ней пришёл из настроек и журнала, которые
        // лежат в доступном пользователю каталоге.
        private async Task<bool> RemoveOldClientCopyAsync(string oldClientPath, string newClientPath)
        {
            if (!Directory.Exists(oldClientPath)) return true;
            if (ClientFolderMover.PathsEqual(oldClientPath, newClientPath)) return false;
            if (!File.Exists(Path.Combine(newClientPath, LauncherPaths.ClientExeName))) return false;
            if (!File.Exists(Path.Combine(oldClientPath, LauncherPaths.ClientExeName))) return false;
            if (!InstallPathGuard.IsClientPathSafe(oldClientPath, _dataFolderPath)) return false;

            bool removed = await ClientFolderMover.TryDeleteDirectoryAsync(oldClientPath);
            if (removed) AddLog($"🧹 Старая папка клиента удалена: {oldClientPath}");
            return removed;
        }

        /// <summary>
        /// Остатки прерванного переноса — при старте, до зачистки остатков установки.
        /// Решение — по указателю (ClientPath): клиент запускается из той папки, на
        /// которую он смотрит, а вторая копия убирается.
        /// </summary>
        private async Task RecoverInterruptedClientMoveAsync()
        {
            string journalPath = LauncherPaths.ClientMoveJournalPath;
            var journal = ClientMoveJournal.Load(journalPath);
            if (journal == null)
            {
                ClientMoveJournal.Delete(journalPath);
                return;
            }

            switch (ClientMoveJournal.Decide(journal, _clientPath))
            {
                case ClientMoveRecoveryAction.RemoveOldCopy:
                    if (await RemoveOldClientCopyAsync(journal.OldClientPath, journal.NewClientPath))
                    {
                        AddLog("🧹 Перенос клиента доделан: старая копия удалена");
                        ClientMoveJournal.Delete(journalPath);
                    }
                    break;

                case ClientMoveRecoveryAction.RemoveNewCopy:
                    // Новую копию удаляем, только если это действительно наша папка
                    // клиента (по имени), а не произвольный путь из журнала.
                    bool cleaned = true;
                    if (IsOwnMoveArtifact(journal.StagingPath, journal.NewClientPath, staging: true))
                        cleaned &= await ClientFolderMover.TryDeleteDirectoryAsync(journal.StagingPath);
                    if (IsOwnMoveArtifact(journal.NewClientPath, journal.NewClientPath, staging: false) &&
                        InstallPathGuard.IsClientPathSafe(journal.NewClientPath, _dataFolderPath))
                        cleaned &= await ClientFolderMover.TryDeleteDirectoryAsync(journal.NewClientPath);
                    AddLog("↩️ Перенос клиента был прерван — клиент остаётся в прежней папке, недоделанная копия убрана");
                    if (cleaned) ClientMoveJournal.Delete(journalPath);
                    break;

                default:
                    ClientMoveJournal.Delete(journalPath);
                    break;
            }
        }

        private static bool IsOwnMoveArtifact(string path, string newClientPath, bool staging)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string name = Path.GetFileName(ClientFolderMover.Normalize(path));
            if (!staging) return string.Equals(name, LauncherPaths.ClientFolderName, StringComparison.OrdinalIgnoreCase);

            string expectedParent = Path.GetDirectoryName(ClientFolderMover.Normalize(newClientPath)) ?? "";
            return ClientFolderMover.PathsEqual(Path.GetDirectoryName(ClientFolderMover.Normalize(path)), expectedParent) &&
                   name.StartsWith($".{LauncherPaths.ClientFolderName}.staging-", StringComparison.OrdinalIgnoreCase);
        }

        // Те же кнопки, что гасит загрузка: перенос делит с ней папку клиента.
        // Текст, который показал перенос, — уже в том виде, в каком его вернула строка состояния
        // (в английском интерфейсе — перевод). По нему в конце понятно, чистить ли строку.
        private string? _moveStatus;

        private void SetMoveStatus(string text)
        {
            txtDownloadStatus.Text = text;
            _moveStatus = txtDownloadStatus.Text;
        }

        private void SetMoveUiBusy(bool busy)
        {
            btnLaunchApp.IsEnabled = !busy;
            btnInstallFromFile.IsEnabled = !busy;
            btnInstallMissing.IsEnabled = !busy;
            btnCancelDownload.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) progressDownload.Value = 0;
        }

        /// <summary>
        /// Подсказка при старте: клиент уже стоит в OneDrive или под «Контролируемым
        /// доступом к папкам». Только запись в журнал — ничего не переносится без кнопки.
        /// </summary>
        private async Task ReportClientLocationRiskAsync()
        {
            try
            {
                if (!File.Exists(Path.Combine(_clientPath, LauncherPaths.ClientExeName))) return;
                var cfa = await InstallLocationRisk.GetCfaStatusAsync();
                string path = _clientPath;
                var risk = await Task.Run(() => InstallLocationRisk.Assess(path, cfa));
                if (!risk.IsRisky) return;
                AddLog("⚠️ Папка клиента может мешать установке обновлений:\n" + risk.Describe() +
                       "\n   Перенести клиент в безопасную папку: Настройки → «Перенести клиент».");
            }
            catch (Exception ex)
            {
                AddLog($"ℹ️ Проверка папки клиента не выполнена: {ex.Message}");
            }
        }
    }
}
