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

        private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token, bool silent = false)
        {
            if (version == null) return;

            // Защита от подмены: качаем только с доверенных доменов GitHub по HTTPS
            if (!DownloadValidator.IsAllowedDownloadHost(version.DownloadUrl))
            {
                AddLog($"⛔ Недоверенный URL загрузки — скачивание отменено: {version.DownloadUrl}");
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
                var downloader = new FallbackDownloader(_httpClient);
                await downloader.DownloadAsync(
                    version.DownloadUrl,
                    version.FallbackUrl,
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
                    switchingToFallback: () =>
                    {
                        AddLog("⚠️ Основной источник недоступен, переключаюсь на резервный...");
                        progressDownload.Value = 0;
                        txtDownloadStatus.Text = "Скачивание: 0%";
                    });

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

        private async void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: загрузка или запуск клиента");
                return;
            }

            string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
            bool clientInstalled = File.Exists(clientExe);

            // Обновление клиента: только когда он установлен, найдено более новое
            // и есть список версий (нужен URL для скачивания). Список версий может
            // отсутствовать офлайн/при GitHub rate-limit — тогда обновление невозможно,
            // но это не должно мешать запуску уже установленного клиента (ниже).
            if (clientInstalled && _clientUpdateAvailable)
            {
                _selectedVersion ??= _availableVersions.FirstOrDefault(v => v.IsLatest);
                if (_selectedVersion != null)
                {
                    AddLog($"⬆ Обновление клиента до {_selectedVersion.Version}...");
                    _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    await DownloadVersionAsync(_selectedVersion, _downloadCts.Token);
                    return;
                }
                AddLog("⚠️ Обновление недоступно (нет списка версий) — запускаю установленный клиент");
            }

            // Уже установленный клиент запускаем всегда — независимо от доступности сети
            // и наличия списка версий. Список нужен только для скачивания/обновления.
            if (clientInstalled)
            {
                LaunchExistingClient(clientExe);
                return;
            }

            // Клиента на диске нет — для скачивания нужен список версий.
            if (_selectedVersion == null)
            {
                var latest = _availableVersions.FirstOrDefault(v => v.IsLatest);
                if (latest == null) { AddLog("❌ Нет доступных версий"); return; }
                _selectedVersion = latest;
                UpdateVersionDisplay(latest);
            }

            AddLog($"📥 Загрузка клиента {_selectedVersion.Version}...");
            // Таймаут страхует от подвисшего (не оборванного) соединения: без него
            // HttpClient с Timeout=Infinite может ждать байты бесконечно, и кнопка
            // «Отмена» — единственный выход. См. тот же паттерн в LauncherUpdateService.
            _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await DownloadVersionAsync(_selectedVersion, _downloadCts.Token);
        }

        // Запуск установленного на диске клиента с подключением watchdog-а.
        // Вынесено из BtnLaunchApp_Click, чтобы запускать клиент из нескольких веток
        // (в т.ч. когда список версий недоступен) без дублирования логики.
        private void LaunchExistingClient(string clientExe)
        {
            AddLog("🚀 Запуск Ven4Tools...");

            var psi = new ProcessStartInfo { FileName = clientExe, UseShellExecute = true };
            try
            {
                // Освобождаем объект предыдущего запуска: EnableRaisingEvents + Exited
                // держат ссылку на Process, без Dispose это утечка
                _clientProcess?.Dispose();
                _clientProcess = null;

                var clientProcess = Process.Start(psi);
                AddLog("✅ Клиент запущен");

                if (clientProcess != null)
                {
                    _clientProcess = clientProcess;
                    _watchdog?.Dispose();
                    _watchdog = new WatchdogService(clientProcess);
                    _watchdog.ClientFrozen += report => Dispatcher.Invoke(() =>
                    {
                        var win = new CrashReportWindow(report) { Owner = this };
                        win.ShowDialog();
                    });
                    _watchdog.ClientKilledWithoutCrash += report => Dispatcher.Invoke(() =>
                    {
                        var win = new CrashReportWindow(report) { Owner = this };
                        win.ShowDialog();
                    });
                    clientProcess.EnableRaisingEvents = true;
                    clientProcess.Exited += (_, _) =>
                    {
                        var wd     = _watchdog;
                        _watchdog  = null;

                        var crashPath = LauncherPaths.CrashReportPath;
                        bool hasFreshCrash = System.IO.File.Exists(crashPath) &&
                            (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(crashPath)).TotalSeconds < 15;

                        // Код 0 — штатное закрытие клиента пользователем, это не «убийство» процесса
                        int exitCode = 0;
                        try { exitCode = clientProcess.ExitCode; } catch { }
                        if (!hasFreshCrash && wd != null && exitCode != 0)
                            wd.ReportKill(exitCode);

                        wd?.Dispose();

                        // Освобождаем объект процесса и очищаем поле,
                        // если за это время не был запущен новый экземпляр
                        if (ReferenceEquals(_clientProcess, clientProcess))
                            _clientProcess = null;
                        clientProcess.Dispose();
                    };
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка запуска: {ex.Message}");
                System.Windows.MessageBox.Show($"Не удалось запустить клиент: {ex.Message}", "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            _downloadCts?.Cancel();
            btnCancelDownload.IsEnabled = false;
        }

        private async void BtnFindClient_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: поиск клиента");
                return;
            }

            btnFindClient.IsEnabled = false;
            AddLog("🔍 Поиск Ven4Tools.exe на диске...");

            try
            {
                var found = await Task.Run(() =>
                {
                    var results = new List<string>();
                    foreach (var root in GetClientSearchRoots())
                    {
                        if (!Directory.Exists(root)) continue;
                        results.AddRange(EnumerateFilesSafe(root, LauncherPaths.ClientExeName));
                    }
                    return results;
                });

                if (found.Count == 0)
                {
                    AddLog("❌ Ven4Tools.exe не найден в стандартных папках");
                    System.Windows.MessageBox.Show(
                        "Ven4Tools.exe не найден в:\n" +
                        "• Program Files / Program Files (x86)\n" +
                        "• Документы / Documents\n" +
                        "• Загрузки / Downloads\n" +
                        "• Рабочий стол\n\n" +
                        "Воспользуйтесь кнопкой «Выбрать папку» для ручного указания пути.",
                        "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var f in found)
                    AddLog($"   📄 {f}");

                string chosen = found[0];
                if (found.Count > 1)
                {
                    var list = string.Join("\n", found.Select((f, i) => $"{i + 1}. {f}"));
                    System.Windows.MessageBox.Show(
                        $"Найдено {found.Count} экземпляра(ов).\nБудет использован первый:\n\n{chosen}\n\nПолный список:\n{list}",
                        "Найдено несколько", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Найдено:\n{chosen}\n\nИспользовать эту папку?",
                        "Ven4Tools найден", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes) return;
                }

                string candidatePath = Path.GetDirectoryName(chosen)!;
                if (!InstallPathGuard.IsClientPathSafe(candidatePath, _dataFolderPath))
                {
                    AddLog($"⛔ Найденный Ven4Tools.exe лежит прямо в защищённой папке ({candidatePath}) — путь не принят");
                    System.Windows.MessageBox.Show(
                        $"Ven4Tools.exe найден прямо в:\n{candidatePath}\n\n" +
                        "Эта папка не может стать папкой установки клиента целиком — при обновлении " +
                        "или удалении её содержимое было бы уничтожено.\n\n" +
                        "Переместите клиент в отдельную подпапку или воспользуйтесь кнопкой «Выбрать папку».",
                        "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _clientPath  = candidatePath;
                _installPath = Path.GetDirectoryName(_clientPath) ?? _clientPath;
                txtInstallPath.Text = _clientPath;
                SaveSettings();
                AddLog($"✅ Папка установки: {_clientPath}");
                CheckExistingClient();
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка поиска: {ex.Message}");
            }
            finally
            {
                btnFindClient.IsEnabled = true;
            }
        }

        // Рекурсивный поиск файла по маске, устойчивый к недоступным подпапкам.
        // Directory.EnumerateFiles(..., AllDirectories) — ленивый: реальный обход идёт
        // при итерации, а не при вызове, поэтому недоступная подпапка где-то в глубине
        // (например C:\Program Files\WindowsApps) молча обрывала бы обход всего корня, и
        // реально существующий Ven4Tools.exe глубже проблемной точки не находился бы.
        // Directory.GetFiles/GetDirectories — НЕ ленивые: бросают сразу, поэтому try/catch
        // вокруг них ловит недоступность каждой папки отдельно, а остальное дерево
        // продолжает сканироваться (тот же паттерн, что AppLaunchResolver.EnumerateLnkFilesSafe
        // в клиенте). Пропуск недоступной папки — штатная ситуация, не логируется.
        private static IEnumerable<string> EnumerateFilesSafe(string root, string searchPattern)
        {
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                string[] files;
                try { files = Directory.GetFiles(dir, searchPattern); }
                catch { continue; } // недоступна сама папка — пропускаем её файлы, не всё дерево
                result.AddRange(files);

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; } // недоступен список подпапок — глубже не идём, но остальное дерево не страдает
                foreach (var sub in subDirs) stack.Push(sub);
            }
            return result;
        }

        private static IEnumerable<string> GetClientSearchRoots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            string? downloads = null;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                downloads = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}")?.ToString();
            }
            catch { }

            if (!string.IsNullOrEmpty(downloads) && Directory.Exists(downloads))
            {
                yield return downloads;
            }
            else
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var name in new[] { "Downloads", "Загрузки" })
                {
                    var path = Path.Combine(userProfile, name);
                    if (Directory.Exists(path)) yield return path;
                }
            }
        }

        private async void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: удаление клиента");
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                "Будет удалено:\n" +
                $"• Папка клиента: {_clientPath}\n" +
                "• Ярлыки на рабочем столе\n" +
                "• Ярлыки в меню Пуск\n" +
                "• Запись автозапуска в реестре\n\n" +
                "Настройки и логи лаунчера сохраняются.\n\n" +
                "Продолжить?",
                "Удаление клиента Ven4Tools",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
            {
                AddLog($"⛔ Удаление отменено: папка клиента указывает на защищённую папку ({_clientPath})");
                System.Windows.MessageBox.Show(
                    $"Папка клиента:\n{_clientPath}\n\n" +
                    "совпадает с защищённой пользовательской папкой (Downloads/Документы/Рабочий стол " +
                    "и т.п.) целиком. Удаление отменено во избежание потери данных.",
                    "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            btnDeleteClient.IsEnabled = false;
            AddLog("🗑️ Удаление клиента...");

            await Task.Run(() =>
            {
                if (Directory.Exists(_clientPath))
                {
                    try { Directory.Delete(_clientPath, true); AddLog("   ✅ Папка клиента удалена"); }
                    catch (Exception ex) { AddLog($"   ⚠️ Папка клиента: {ex.Message}"); }
                }
                else
                {
                    AddLog("   ℹ️ Папка клиента не найдена");
                }

                string[] desktops = {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                };
                string[] startMenuRoots = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
                };
                ClientShortcutCleaner.Clean(desktops, startMenuRoots);
                AddLog("   ✅ Ярлыки клиента проверены");

                try
                {
                    using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    runKey?.DeleteValue("Ven4Tools", throwOnMissingValue: false);
                    runKey?.DeleteValue("Ven4Tools Client", throwOnMissingValue: false);
                    AddLog("   ✅ Записи автозапуска клиента удалены");
                }
                catch (Exception ex) { AddLog($"   ⚠️ Реестр: {ex.Message}"); }

                // Корневую папку %LocalAppData%\Ven4Tools не трогаем: в ней лежат
                // настройки и логи работающего лаунчера. После удаления пересоздаём
                // папку клиента, чтобы состояние осталось консистентным.
                try { Directory.CreateDirectory(_clientPath); } catch { }
            });

            Dispatcher.Invoke(() =>
            {
                SetLaunchButtonState(LaunchButtonState.Download);
                btnDeleteClient.IsEnabled = true;
            });

            AddLog("✅ Удаление завершено");
        }
    }
}
