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
        // ── Скачивание ────────────────────────────────────────────────────────

        private async void BtnDownloadOffice_Click(object sender, RoutedEventArgs e)
        {
            if (cmbOfficeLanguage.SelectedItem == null) return;

            var (displayName, productId) = GetSelectedVersion();
            string lang = cmbOfficeLanguage.SelectedItem.ToString()!;

            // Удаляем предыдущий скачанный установщик, если он остался
            if (_downloadedFilePath != null)
            {
                try { File.Delete(_downloadedFilePath); } catch { }
                _downloadedFilePath = null;
            }

            btnDownloadOffice.IsEnabled = false;
            btnInstallOffice.IsEnabled  = false;
            btnCancelOffice.IsEnabled   = true;
            btnCancelOffice.Visibility  = Visibility.Visible;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            SetProgress(true, "⏳ Подготовка...", 0, "");
            AppLogger.Write($"\n📥 Скачивание {displayName} ({lang})...");

            string tempFile = Path.Combine(Path.GetTempPath(), $"OfficeSetup_{Guid.NewGuid():N}.exe");

            try
            {
                string downloadUrl = string.Format(officeDirectLinks[productId], lang);
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                using var src = await response.Content.ReadAsStreamAsync(token);
                using var dst = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                var  buf      = new byte[65536];
                int  read;
                long total    = 0;
                long? size    = response.Content.Headers.ContentLength;
                int  lastPct  = -1;

                // Sliding-таймаут простоя между чтениями — тот же класс риска, что и
                // в InstallationService/FallbackDownloader/OfflineService: зависший или
                // крайне медленный сервер иначе вешал бы загрузку до ручной отмены.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                idleCts.CancelAfter(TimeSpan.FromSeconds(60));

                while ((read = await src.ReadAsync(buf, idleCts.Token)) > 0)
                {
                    idleCts.CancelAfter(TimeSpan.FromSeconds(60));
                    await dst.WriteAsync(buf, 0, read, token);
                    total += read;

                    if (size.HasValue)
                    {
                        int pct = (int)(total * 100.0 / size.Value);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            SetProgress(true,
                                $"📥 Скачивание: {pct}%", pct,
                                $"{(double)total / 1_048_576:F1} / {(double)size.Value / 1_048_576:F1} МБ");
                        }
                    }
                    else
                    {
                        SetProgress(true, "📥 Скачивание...", 0,
                            $"{(double)total / 1_048_576:F1} МБ");
                    }
                }

                var fi = new FileInfo(tempFile);
                AppLogger.Write($"✅ Скачано: {fi.Length / 1_048_576.0:F1} МБ");
                SetProgress(true, "✅ Скачано! Нажмите «Установить»", 100,
                    $"{fi.Length / 1_048_576.0:F1} МБ");

                _downloadedFilePath = tempFile;
                btnInstallOffice.IsEnabled = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Idle-таймаут (не token) падает в общий catch ниже — показывается как
                // обычная ошибка загрузки, а не как «отменено пользователем».
                AppLogger.Write("⏹️ Скачивание отменено");
                SetProgress(true, "⏹️ Отменено", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка скачивания: {ex.Message}");
                SetProgress(true, "❌ Ошибка", 0, "");
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                MessageBox.Show("Не удалось скачать Office. Проверьте подключение к интернету и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                Dispatcher.Invoke(() =>
                {
                    btnDownloadOffice.IsEnabled = true;
                    btnCancelOffice.IsEnabled   = false;
                });
            }
        }
    }
}
