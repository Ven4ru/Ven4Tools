using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

        private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token)
        {
            if (version == null) return;

            // Защита от подмены: качаем только с доверенных доменов GitHub по HTTPS
            if (!DownloadValidator.IsAllowedDownloadHost(version.DownloadUrl))
            {
                AddLog($"⛔ Недоверенный URL загрузки — скачивание отменено: {version.DownloadUrl}");
                return;
            }

            AddLog($"📥 Скачивание клиента {version.Version}...");

            string tempZip     = Path.Combine(Path.GetTempPath(), $"Ven4Tools_Client_{version.Version}_{Guid.NewGuid()}.zip");
            string extractPath = Path.Combine(Path.GetTempPath(), $"extract_{Guid.NewGuid()}");
            string? clientBackup  = null;
            bool copyCompleted = false;

            progressDownload.Value    = 0;
            txtDownloadStatus.Text    = "Скачивание: 0%";
            btnCancelDownload.Visibility = Visibility.Visible;
            btnLaunchApp.IsEnabled    = false;

            try
            {
                using var response = await _httpClient.GetAsync(version.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var bytesRead  = 0L;
                var buffer     = new byte[81920];

                using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    using var stream = await response.Content.ReadAsStreamAsync(token);
                    int bytes;
                    while ((bytes = await stream.ReadAsync(buffer.AsMemory(), token)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, bytes), token);
                        bytesRead += bytes;
                        if (totalBytes > 0)
                        {
                            var percent = (int)((double)bytesRead / totalBytes * 100);
                            progressDownload.Value = percent;
                            txtDownloadStatus.Text = $"Скачивание: {percent}%";
                        }
                    }
                    await fs.FlushAsync(token);
                }

                token.ThrowIfCancellationRequested();

                // Проверка целостности: если сервер сообщил размер, а получили меньше —
                // соединение оборвалось, архив битый.
                if (totalBytes > 0 && bytesRead != totalBytes)
                    throw new IOException(
                        $"Загрузка неполная: получено {bytesRead} из {totalBytes} байт. Проверьте соединение и повторите.");

                txtDownloadStatus.Text = "Распаковка...";
                await Task.Delay(1000, token);

                bool extracted = false;
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        Directory.CreateDirectory(extractPath);
                        ZipFile.ExtractToDirectory(tempZip, extractPath, true);
                        extracted = true;
                        AddLog($"✅ Распаковано с попытки {attempt}");
                        break;
                    }
                    catch (IOException ex) when (attempt < 5)
                    {
                        AddLog($"⚠️ Попытка распаковки {attempt}/5: {ex.Message}");
                        await Task.Delay(2000, token);
                    }
                }
                if (!extracted) throw new IOException("Не удалось распаковать архив после 5 попыток");

                token.ThrowIfCancellationRequested();

                // Нельзя перезаписывать файлы запущенного клиента.
                if (IsClientRunning())
                {
                    txtDownloadStatus.Text = "Клиент запущен";
                    AddLog("⚠️ Ven4Tools запущен — закройте клиент перед обновлением");
                    System.Windows.MessageBox.Show(
                        "Ven4Tools сейчас запущен.\n\nЗакройте приложение и повторите установку обновления.",
                        "Клиент запущен", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                txtDownloadStatus.Text = "Копирование файлов...";

                if (Directory.Exists(_clientPath))
                {
                    // Бэкап: если копирование оборвётся — восстановим рабочую версию.
                    clientBackup = Path.Combine(Path.GetTempPath(), $"ven4_client_backup_{Guid.NewGuid():N}");
                    CopyDirectory(_clientPath, clientBackup);

                    foreach (var file in Directory.GetFiles(_clientPath)) try { File.Delete(file); } catch { }
                    foreach (var dir in Directory.GetDirectories(_clientPath)) try { Directory.Delete(dir, true); } catch { }
                }

                var allFiles  = Directory.GetFiles(extractPath, "*", SearchOption.AllDirectories);
                int fileCount = 0;
                foreach (var file in allFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string relativePath = file.Substring(extractPath.Length + 1);
                    string targetFile   = Path.Combine(_clientPath, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                    File.Copy(file, targetFile, true);
                    if (++fileCount % 20 == 0)
                        txtDownloadStatus.Text = $"Копирование: {fileCount}/{allFiles.Length} файлов";
                }
                copyCompleted = true;

                txtDownloadStatus.Text = "Готово";
                progressDownload.Value = 100;
                AddLog($"✅ Клиент {version.Version} скачан и распакован");
                version.IsInstalled = true;

                btnLaunchApp.Content    = "🚀 Запустить Ven4Tools";
                btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));

                System.Windows.MessageBox.Show(
                    $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
                    "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                txtDownloadStatus.Text = "Отменено";
                progressDownload.Value = 0;
                AddLog("⏹ Загрузка отменена");
                if (!copyCompleted) RestoreClientBackup(clientBackup);
            }
            catch (Exception ex)
            {
                txtDownloadStatus.Text = "Ошибка";
                AddLog($"❌ Ошибка скачивания: {ex.Message}");
                if (!copyCompleted) RestoreClientBackup(clientBackup);
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
                try { if (clientBackup != null && Directory.Exists(clientBackup)) Directory.Delete(clientBackup, true); } catch { }
                btnCancelDownload.Visibility = Visibility.Collapsed;
                btnCancelDownload.IsEnabled  = true;
                btnLaunchApp.IsEnabled       = true;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = file.Substring(sourceDir.Length + 1);
                string target       = Path.Combine(destDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        private void RestoreClientBackup(string? clientBackup)
        {
            if (clientBackup == null || !Directory.Exists(clientBackup)) return;
            try
            {
                AddLog("↩️ Восстановление предыдущей версии клиента...");
                if (Directory.Exists(_clientPath))
                {
                    foreach (var file in Directory.GetFiles(_clientPath)) try { File.Delete(file); } catch { }
                    foreach (var dir in Directory.GetDirectories(_clientPath)) try { Directory.Delete(dir, true); } catch { }
                }
                CopyDirectory(clientBackup, _clientPath);
                AddLog("✅ Предыдущая версия клиента восстановлена");
            }
            catch (Exception rex)
            {
                AddLog($"⚠️ Не удалось восстановить предыдущую версию: {rex.Message}");
            }
        }

        private async void BtnLaunchApp_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedVersion == null)
            {
                var latest = _availableVersions.FirstOrDefault(v => v.IsLatest);
                if (latest == null) { AddLog("❌ Нет доступных версий"); return; }
                _selectedVersion         = latest;
                cmbVersions.SelectedItem = latest;
            }

            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");

            if (File.Exists(clientExe))
            {
                AddLog($"🚀 Запуск Ven4Tools {_selectedVersion.Version}...");

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

                            var crashPath = System.IO.Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Ven4Tools", "crash_last.json");
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
                return;
            }

            AddLog($"📥 Загрузка клиента {_selectedVersion.Version}...");
            _downloadCts = new CancellationTokenSource();
            await DownloadVersionAsync(_selectedVersion, _downloadCts.Token);
        }

        private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            _downloadCts?.Cancel();
            btnCancelDownload.IsEnabled = false;
        }

        private async void BtnFindClient_Click(object sender, RoutedEventArgs e)
        {
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
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(root, "Ven4Tools.exe", SearchOption.AllDirectories))
                                results.Add(file);
                        }
                        catch { }
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

                _clientPath  = Path.GetDirectoryName(chosen)!;
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
                foreach (var desktop in desktops)
                {
                    if (string.IsNullOrEmpty(desktop)) continue;
                    foreach (var name in new[] { "Ven4Tools.lnk", "Ven4Tools Launcher.lnk", "Ven4Tools Client.lnk" })
                    {
                        string path = Path.Combine(desktop, name);
                        if (File.Exists(path)) { try { File.Delete(path); } catch { } }
                    }
                }
                AddLog("   ✅ Ярлыки рабочего стола проверены");

                string[] startMenuRoots = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
                };
                foreach (var root in startMenuRoots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string ven4Dir = Path.Combine(root, "Ven4Tools");
                    if (Directory.Exists(ven4Dir)) { try { Directory.Delete(ven4Dir, true); } catch { } }
                    foreach (var name in new[] { "Ven4Tools.lnk", "Ven4Tools Launcher.lnk" })
                    {
                        string path = Path.Combine(root, name);
                        if (File.Exists(path)) { try { File.Delete(path); } catch { } }
                    }
                }
                AddLog("   ✅ Ярлыки меню Пуск проверены");

                try
                {
                    using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                    runKey?.DeleteValue("Ven4Tools", throwOnMissingValue: false);
                    runKey?.DeleteValue("Ven4Tools.Launcher", throwOnMissingValue: false);
                    runKey?.DeleteValue("Ven4Tools Client", throwOnMissingValue: false);
                    AddLog("   ✅ Записи автозапуска удалены");
                }
                catch (Exception ex) { AddLog($"   ⚠️ Реестр: {ex.Message}"); }

                // Корневую папку %LocalAppData%\Ven4Tools не трогаем: в ней лежат
                // настройки и логи работающего лаунчера. После удаления пересоздаём
                // папку клиента, чтобы состояние осталось консистентным.
                try { Directory.CreateDirectory(_clientPath); } catch { }
            });

            Dispatcher.Invoke(() =>
            {
                btnLaunchApp.Content    = "📥 Загрузить Ven4Tools";
                btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 140, 0));
                btnDeleteClient.IsEnabled = true;
            });

            AddLog("✅ Удаление завершено");
        }
    }
}
