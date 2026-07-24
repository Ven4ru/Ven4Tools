using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class OfficeTab : UserControl
    {
        // ── Установка ─────────────────────────────────────────────────────────

        private async void BtnInstallOffice_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadedFilePath == null || !File.Exists(_downloadedFilePath))
            {
                AppLogger.Write("⚠️ Файл установщика не найден — скачайте снова.");
                btnInstallOffice.IsEnabled = false;
                return;
            }
            string installerPath = _downloadedFilePath;

            var (displayName, _) = GetSelectedVersion();

            btnInstallOffice.IsEnabled  = false;
            btnDownloadOffice.IsEnabled = false;
            btnCancelOffice.IsEnabled   = true;
            btnCancelOffice.Visibility  = Visibility.Visible;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            bool regionChanged = false;

            SetProgress(true, "⏳ Подготовка установки...", 0, "");
            AppLogger.Write($"\n🚀 Установка {displayName}...");

            try
            {
                SetPhase("🔐 Проверка подлинности установщика...");

                // FileShare.Read держим открытым от проверки подписи до запуска
                // установщика — запрещает подмену файла другим процессом того же
                // пользователя в этом окне (TOCTOU), как в MainWindow.Components.cs
                // (InstallWebView2Async/InstallVcRedistAsync лаунчера). Хендл
                // закрывается явно (не using var на весь блок), чтобы не держать
                // файл заблокированным для удаления в ветке отказа проверки ниже.
                var installerHandle = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (!AuthenticodeVerifier.IsSignedByMicrosoft(installerPath, out string signatureError))
                {
                    installerHandle.Dispose();
                    AppLogger.Write("❌ Не удалось подтвердить подлинность установщика Microsoft — скачайте заново");
                    AppLogger.Write($"   Причина: {signatureError}");
                    TryDeleteDownloadedInstaller();
                    SetProgress(true, "❌ Подлинность не подтверждена", 0, "Скачайте установщик заново.");
                    MessageBox.Show("Не удалось подтвердить подлинность установщика Microsoft — скачайте заново.",
                        "Проверка установщика", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                AppLogger.Write("✅ Подпись установщика Microsoft подтверждена");

                SaveRegion();
                regionChanged = true; // до SetRegionUS — чтобы finally откатил даже при исключении внутри
                SetRegionUS();
                AppLogger.Write("🌎 Регион переключён на US (GeoID: 244, CountryCode: US)");

                SetPhase("🚀 Запуск установщика...");
                var existingPids = GetC2RProcessPids();

                // Последняя точка, где отмена ещё безопасна: если пользователь нажал
                // «Отмена» на этапе проверки подписи — прерываемся ДО запуска установщика
                // (регион восстановит finally). После Process.Start отмена уже недоступна.
                token.ThrowIfCancellationRequested();

                using var bootstrapper = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = installerPath,
                        UseShellExecute = true,
                        Verb            = "runas"
                    });
                // ShellExecuteEx уже открыл/запустил файл к моменту возврата
                // из Process.Start — хендл-защита от подмены больше не нужна.
                installerHandle.Dispose();

                if (bootstrapper != null)
                {
                    // M3: elevated-процесс установщика уже запущен — реальную установку
                    // отменить нельзя (регион будет восстановлен только после её завершения).
                    // Прячем «Отмена», чтобы UI не обещал невозможного.
                    Dispatcher.Invoke(() =>
                    {
                        btnCancelOffice.IsEnabled  = false;
                        btnCancelOffice.Visibility = Visibility.Collapsed;
                    });
                    SetPhase("⚙️ Установка Office запущена — отменить нельзя, дождитесь завершения");

                    await bootstrapper.WaitForExitAsync(token);
                    if (bootstrapper.ExitCode != 0)
                    {
                        AppLogger.Write($"❌ Установщик завершился с кодом {bootstrapper.ExitCode}");
                        AppLogger.Write("   Вероятная причина: CDN Microsoft заблокирован в вашем регионе.");
                        AppLogger.Write("   Попробуйте использовать VPN и повторить установку.");
                        SetProgress(true, $"❌ Сбой установки (код {bootstrapper.ExitCode})", 0,
                            "CDN Microsoft может быть недоступен. Попробуйте VPN.");
                        return;
                    }
                }

                token.ThrowIfCancellationRequested();

                SetPhase("⚙️ Установка Office... не закрывайте приложение");
                AppLogger.Write("⏳ Ожидаем запуск C2R-установщика...");

                using var installProc = await WaitForC2RProcess(existingPids, TimeSpan.FromMinutes(3), token);

                if (installProc == null)
                {
                    AppLogger.Write("⚠️ Процесс установки не обнаружен — возможно Office уже установлен или завершился мгновенно");
                }
                else
                {
                    AppLogger.Write($"🔍 Мониторинг: {installProc.ProcessName} (PID {installProc.Id})");
                    SetProgress(true, "⚙️ Установка Office...", 0, "Идёт установка, пожалуйста подождите...");
                    progressOffice.IsIndeterminate = true;
                    await MonitorInstallation(installProc, token);
                    progressOffice.IsIndeterminate = false;
                }

                token.ThrowIfCancellationRequested();

                RestoreRegion();
                regionChanged = false;
                AppLogger.Write("✅ Установка завершена — регион восстановлен");
                SetProgress(true, "✅ Офис установлен!", 100, "Регион восстановлен");

                if (chkSaveInstaller.IsChecked != true)
                {
                    TryDeleteDownloadedInstaller();
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Write("⏹️ Установка отменена");
                SetProgress(true, "⏹️ Отменено", 0, "");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка установки: {ex.Message}");
                SetProgress(true, "❌ Ошибка установки", 0, "");
                MessageBox.Show("Не удалось установить Office. Попробуйте ещё раз или установите вручную.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (regionChanged)
                {
                    RestoreRegion();
                    AppLogger.Write("🔁 Регион восстановлен (аварийный сброс)");
                }
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Dispatcher.Invoke(() =>
                {
                    btnDownloadOffice.IsEnabled = true;
                    btnCancelOffice.IsEnabled   = false;
                    btnInstallOffice.IsEnabled  = _downloadedFilePath != null && File.Exists(_downloadedFilePath);
                });
            }
        }

        private void TryDeleteDownloadedInstaller()
        {
            if (_downloadedFilePath == null)
                return;

            try { File.Delete(_downloadedFilePath); } catch { }
            _downloadedFilePath = null;
        }

        // ── Помощники для процессов C2R ───────────────────────────────────────

        private static HashSet<int> GetC2RProcessPids()
        {
            var names = new[] { "officec2rclient", "OfficeClickToRun" };
            var pids  = new HashSet<int>();
            foreach (var name in names)
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    using (p) pids.Add(p.Id);
            return pids;
        }

        private static async Task<System.Diagnostics.Process?> WaitForC2RProcess(
            HashSet<int> existingPids, TimeSpan timeout, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + timeout;
            var names    = new[] { "officec2rclient", "OfficeClickToRun" };

            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                foreach (var name in names)
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        // Найденный процесс возвращаем (его освобождает вызывающий),
                        // остальные снимки процессов освобождаем сразу.
                        if (!existingPids.Contains(p.Id))
                            return p;
                        p.Dispose();
                    }

                await Task.Delay(2000, token);
            }
            return null;
        }

        private async Task MonitorInstallation(System.Diagnostics.Process proc, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(60);
            var elapsed  = System.Diagnostics.Stopwatch.StartNew();

            while (!proc.HasExited && DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(5000, token);
                SetDetail($"Установка идёт {elapsed.Elapsed:mm\\:ss}...");
            }

            if (!proc.HasExited)
                AppLogger.Write("⚠️ Таймаут ожидания — продолжаем без подтверждения");
        }
    }
}
