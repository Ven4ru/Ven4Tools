using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: выбор папки");
                return;
            }

            using var dialog = new FolderBrowserDialog
            {
                Description      = "Выберите папку для установки Ven4Tools",
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _installPath = dialog.SelectedPath;
                _clientPath  = Path.Combine(_installPath, "Ven4Tools_Client");
                Directory.CreateDirectory(_clientPath);
                txtInstallPath.Text = _clientPath;
                SaveSettings();
                AddLog($"📁 Папка установки изменена: {_clientPath}");
                CheckExistingClient();
            }
        }

        // Осиротевшие ".Ven4Tools_Client.staging-*" / "Ven4Tools_Client.backup-*" — остаются
        // рядом с папкой клиента, если процесс убит посреди DownloadVersionAsync/
        // TransactionalDirectoryInstaller.Install. Однократная зачистка при старте:
        // единственный экземпляр лаунчера (см. App.SingleInstance) гарантирует, что
        // на момент запуска эти каталоги не могут принадлежать активной операции.
        private static void CleanupStaleInstallArtifacts(string clientPath)
        {
            try
            {
                string fullClientPath = Path.GetFullPath(clientPath);
                string? parent = Path.GetDirectoryName(fullClientPath);
                if (parent == null || !Directory.Exists(parent)) return;

                string clientName = Path.GetFileName(fullClientPath);
                string stagingPrefix = $".{clientName}.staging-";
                string backupPrefix = $"{clientName}.backup-";

                // Материализуем список: ниже возможно перемещение бэкапа обратно в
                // папку клиента (внутри того же родителя), а менять каталог во время
                // ленивого перечисления небезопасно.
                foreach (string dir in Directory.EnumerateDirectories(parent).ToList())
                {
                    string name = Path.GetFileName(dir);
                    bool isStaging = name.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase);
                    bool isBackup = name.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase);
                    if (!isStaging && !isBackup) continue;

                    // Есть бэкап предыдущей версии, а самой папки клиента нет — значит
                    // установку прервали между Move(target→backup) и Move(staging→target).
                    // Это не «мусор»: в бэкапе лежит единственная рабочая версия, поэтому
                    // восстанавливаем её обратно в target, а не удаляем.
                    if (isBackup && !Directory.Exists(fullClientPath))
                    {
                        try { Directory.Move(dir, fullClientPath); }
                        catch { /* не удалось восстановить — оставляем бэкап на месте, не удаляя */ }
                        continue;
                    }

                    // target на месте (установка завершилась) либо это staging —
                    // такой каталог действительно осиротевший, его можно удалить.
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* занято/уже удалено — не мешаем запуску лаунчера */ }
                }
            }
            catch { /* зачистка необязательна для работы лаунчера */ }
        }

        // Строит цепочку источников для скачивания клиента: CDN-домен → CDN прямой IP →
        // хостинг-зеркало → GitHub, с учётом выбранного пользователем предпочтения.
        // Если CDN не знал версию (только GithubUrl) — цепочка вырождается в один GitHub.
        private List<DownloadCandidate> BuildClientCandidates(ClientVersionInfo version)
        {
            string ip = CdnService.LastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
            // Для клиентских загрузок клиент с бесконечным таймаутом (как _httpClient):
            // длительность ограничивается CancellationToken на месте вызова.
            HttpClient ipPinned = IpPinnedHttpClientFactory.GetOrCreate(ip, Timeout.InfiniteTimeSpan);
            return FallbackDownloader.BuildCandidates(
                _downloadSource,
                version.CdnUrl,
                version.MirrorHostingUrl,
                version.GithubUrl ?? version.DownloadUrl,
                _httpClient,
                ipPinned);
        }

        private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token, bool silent = false)
        {
            if (version == null) return;

            // Цепочка источников (CDN-домен → CDN прямой IP → хостинг-зеркало → GitHub)
            // с учётом выбранного предпочтения. Защита от подмены (только доверенные
            // хосты по HTTPS) выполняется внутри FallbackDownloader для каждого кандидата.
            var candidates = BuildClientCandidates(version);
            if (candidates.Count == 0)
            {
                AddLog($"⛔ Нет доверенных источников загрузки — скачивание отменено: {version.DownloadUrl}");
                return;
            }

            AddLog($"📥 Скачивание клиента {version.Version}...");

            string tempZip = Path.Combine(
                Path.GetTempPath(),
                $"Ven4Tools_Client_{version.Version}_{Guid.NewGuid():N}.zip");
            string clientParent = Path.GetDirectoryName(Path.GetFullPath(_clientPath))
                ?? throw new InvalidOperationException("Не удалось определить каталог установки.");
            string extractPath = Path.Combine(
                clientParent,
                $".Ven4Tools_Client.staging-{Guid.NewGuid():N}");

            progressDownload.Value    = 0;
            txtDownloadStatus.Text    = "Скачивание: 0%";
            btnCancelDownload.Visibility = Visibility.Visible;
            btnLaunchApp.IsEnabled    = false;
            SetOperationStage(1); // Загрузка

            try
            {
                var downloader = new FallbackDownloader();
                // using держит FileShare.Read-хендл на tempZip открытым до конца метода
                // (в т.ч. через SafeZipExtractor.ExtractAsync ниже) — закрывает окно TOCTOU
                // между проверкой SHA256 внутри DownloadAsync и распаковкой архива.
                using var downloadResult = await downloader.DownloadAsync(
                    candidates,
                    tempZip,
                    token,
                    version.ExpectedSha256,
                    progress: (received, total) =>
                    {
                        if (total is > 0)
                        {
                            int percent = (int)((double)received / total.Value * 100);
                            progressDownload.Value = percent;
                            txtDownloadStatus.Text = $"Скачивание: {percent}%";
                        }
                    },
                    switchingTo: label =>
                    {
                        AddLog($"⚠️ Предыдущий источник недоступен, переключаюсь: {label}...");
                        progressDownload.Value = 0;
                        txtDownloadStatus.Text = "Скачивание: 0%";
                    });
                string usedSource = downloadResult.SourceLabel;
                AddLog($"📥 Источник загрузки: {usedSource}");

                token.ThrowIfCancellationRequested();

                // SHA256 проверяется загрузчиком до принятия файла; при несовпадении
                // основного источника автоматически пробуется резервный. Отсутствие
                // хеша в манифесте (CDN недоступен в момент загрузки списка версий,
                // либо CDN ещё не знает именно эту версию — окно между релизом на
                // GitHub и cdn-deploy) раньше трактовалось как предупреждение и
                // установка продолжалась без независимой проверки целостности —
                // fail-open. Самообновление лаунчера (LauncherUpdateService) в тех
                // же условиях строго отказывает; здесь приводим клиентский путь
                // к той же fail-closed политике.
                SetOperationStage(2); // Проверка целостности
                if (!string.IsNullOrEmpty(version.ExpectedSha256))
                {
                    txtDownloadStatus.Text = "Проверка целостности...";
                    AddLog("🔒 Целостность подтверждена (SHA256)");
                }
                else
                {
                    txtDownloadStatus.Text = "Целостность не подтверждена";
                    SetOperationStage(0);
                    AddLog($"⛔ Для версии {version.Version} нет подтверждённого SHA256 (CDN недоступен или ещё не знает эту версию) — установка отменена");
                    if (!silent)
                        System.Windows.MessageBox.Show(
                            $"Не удалось подтвердить целостность архива версии {version.Version} — CDN недоступен, " +
                            "или версия ещё не попала в подписанный манифест.\n\nПопробуйте позже, когда CDN " +
                            "синхронизируется, или обратитесь к автору проекта.",
                            "Целостность не подтверждена", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SetOperationStage(3); // Распаковка
                txtDownloadStatus.Text = "Распаковка...";
                await SafeZipExtractor.ExtractAsync(tempZip, extractPath, token);
                AddLog("✅ Архив безопасно распакован");

                token.ThrowIfCancellationRequested();

                // Нельзя перезаписывать файлы запущенного клиента — просим закрыться
                // штатно. В тихом (фоновом) автообновлении блокирующий диалог
                // «ниоткуда» при свёрнутом в трей лаунчере недопустим (плохой UX):
                // просто откладываем установку до следующего тика фоновой проверки
                // или до ручного запуска пользователем. Вопрос с диалогом оставлен
                // только для явного ручного пути (silent == false).
                if (IsClientRunning())
                {
                    txtDownloadStatus.Text = "Клиент запущен";

                    if (silent)
                    {
                        SetOperationStage(0);
                        AddLog("⏸ Автообновление отложено: клиент запущен (обновим, когда он будет закрыт)");
                        return;
                    }

                    var answer = System.Windows.MessageBox.Show(
                        "Ven4Tools сейчас запущен.\n\nЗакрыть клиент сейчас, чтобы установить обновление?",
                        "Клиент запущен", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (answer != MessageBoxResult.Yes)
                    {
                        SetOperationStage(0);
                        AddLog("⏹ Обновление отменено — клиент не закрыт");
                        return;
                    }

                    AddLog("🔒 Закрываю клиент перед установкой обновления...");
                    if (!await TryCloseRunningClientAsync())
                    {
                        txtDownloadStatus.Text = "Клиент запущен";
                        SetOperationStage(0);
                        AddLog("⚠️ Клиент не закрылся за отведённое время — обновление отменено");
                        System.Windows.MessageBox.Show(
                            "Не удалось закрыть клиент автоматически (возможно, он свёрнут в трей).\n\n" +
                            "Закройте его вручную и повторите установку обновления.",
                            "Клиент не закрылся", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    AddLog("✅ Клиент закрыт, продолжаю установку");
                }

                if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
                {
                    txtDownloadStatus.Text = "Ошибка пути";
                    SetOperationStage(0);
                    AddLog($"⛔ Папка установки клиента пересекается с папкой данных — обновление отменено: {_clientPath}");
                    if (!silent)
                        System.Windows.MessageBox.Show(
                            $"Папка установки клиента:\n{_clientPath}\n\nсовпадает или вложена в папку данных Ven4Tools. " +
                            "Обновление отменено во избежание потери настроек.\n\nВыберите другую папку установки.",
                            "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                SetOperationStage(4); // Установка файлов
                txtDownloadStatus.Text = "Установка файлов...";
                var installer = new TransactionalDirectoryInstaller();
                installer.Install(extractPath, _clientPath, token);

                SetOperationStage(5); // Готово
                txtDownloadStatus.Text = "Готово";
                progressDownload.Value = 100;
                AddLog($"✅ Клиент {version.Version} скачан и распакован");

                SetLaunchButtonState(LaunchButtonState.Launch);
                _clientUpdateAvailable  = false; // клиент теперь актуален — иначе следующий клик по «Запустить» повторно переустановит ту же версию

                if (!silent)
                    System.Windows.MessageBox.Show(
                        $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                txtDownloadStatus.Text = "Отменено";
                progressDownload.Value = 0;
                SetOperationStage(0);
                AddLog("⏹ Загрузка отменена");
            }
            catch (Exception ex)
            {
                txtDownloadStatus.Text = "Ошибка";
                SetOperationStage(0);
                AddLog($"❌ Ошибка скачивания: {ex.Message}");
                if (!silent)
                    System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Очистка временных файлов в любом исходе: успех, ошибка, отмена
                // и ранний return (клиент запущен). Несколько попыток на случай,
                // если файл ещё держит распаковщик/антивирус.
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(tempZip)) File.Delete(tempZip);
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        try { await Task.Delay(1000); } catch { }
                    }
                    catch { break; }
                }
                btnCancelDownload.Visibility = Visibility.Collapsed;
                btnCancelDownload.IsEnabled  = true;
                btnLaunchApp.IsEnabled       = true;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

    }
}
