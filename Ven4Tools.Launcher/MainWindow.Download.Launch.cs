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
    }
}
