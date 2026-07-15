using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        // Единый HttpClient для скачивания установщиков в кэш — переиспользуется,
        // чтобы не плодить сокеты (socket exhaustion) при каждом запуске загрузки.
        private static readonly HttpClient _httpClient = CreateCacheHttpClient();

        private static HttpClient CreateCacheHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools");
            return client;
        }

        private CancellationTokenSource? _cacheCts;

        private List<CacheAppItem> _cacheAppItems = new();

        private sealed class CacheAppItem
        {
            public string Id          { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool   IsSelected  { get; set; }
            public string DownloadUrl { get; set; } = "";
            public string Sha256      { get; set; } = "";
        }

        private void UpdateCacheStats()
        {
            var (count, sizeMB) = OfflineService.GetCacheStats();
            txtCacheStats.Text = count == 0
                ? "Кэш пуст"
                : $"{count} файлов · {sizeMB} МБ  ({OfflineService.CachePath})";
        }

        private void LoadCacheAppsList()
        {
            var catalog = CatalogLoaderService.LoadedCatalog;
            if (catalog == null || catalog.Apps.Count == 0)
            {
                listCacheApps.ItemsSource = null;
                return;
            }

            // Кэшируются только приложения с прямой ссылкой и контрольной суммой SHA256.
            // Источник winget не поддерживает докачивание установщика в кэш, поэтому
            // winget-only приложения в этот список не попадают.
            _cacheAppItems = catalog.Apps
                .Where(a => HashHelper.HasExpectedHash(a.Sha256) &&
                            !string.IsNullOrEmpty(a.DownloadUrl))
                .OrderBy(a => a.Name)
                .Select(a => new CacheAppItem
                {
                    Id           = a.Id,
                    DisplayName  = $"{a.Name}  [{a.Category}]{(OfflineService.HasCachedInstaller(a.Id) ? " ✅" : "")}",
                    DownloadUrl  = a.DownloadUrl,
                    Sha256       = a.Sha256!
                })
                .ToList();

            listCacheApps.ItemsSource = _cacheAppItems;
        }

        private void TxtCacheAppFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = txtCacheAppFilter.Text.Trim().ToLowerInvariant();
            listCacheApps.ItemsSource = string.IsNullOrEmpty(q)
                ? _cacheAppItems
                : _cacheAppItems.Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void BtnCacheSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _cacheAppItems) item.IsSelected = true;
            listCacheApps.Items.Refresh();
        }

        private void BtnCacheSelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _cacheAppItems) item.IsSelected = false;
            listCacheApps.Items.Refresh();
        }

        private void BtnBrowseCachePath_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description      = "Выберите папку для кэша установщиков",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                txtOfflineCachePath.Text = dlg.SelectedPath;
                ProfileService.Current.OfflineCachePath = dlg.SelectedPath;
                ProfileService.Save();
                UpdateCacheStats();
            }
        }

        private void BtnOpenCacheFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OfflineService.EnsureCacheDir();
                Process.Start(new ProcessStartInfo(OfflineService.CachePath) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Write($"❌ {ex.Message}"); }
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Удалить все кэшированные установщики?",
                "Очистка кэша", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            OfflineService.ClearCache();
            UpdateCacheStats();
            LoadCacheAppsList();
            AppLogger.Write("✅ Кэш очищен");
        }

        private async void BtnDownloadToCache_Click(object sender, RoutedEventArgs e)
        {
            var selected = _cacheAppItems.Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Не выбрано ни одного приложения.", "Нет выбора",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _cacheCts = new CancellationTokenSource();
            var token = _cacheCts.Token;

            btnDownloadToCache.IsEnabled       = false;
            btnCancelCacheDownload.Visibility  = Visibility.Visible;
            progressCache.Visibility           = Visibility.Visible;
            txtCacheLog.Visibility             = Visibility.Visible;
            txtCacheLog.Clear();

            // Вся подготовка — внутри try: исключение в async void-обработчике
            // (например, недопустимый путь кэша в EnsureCacheDir) уронило бы всё приложение
            try
            {
                SaveOfflineSettings();
                OfflineService.EnsureCacheDir();

                var http = _httpClient;

                int done = 0, total = selected.Count, errors = 0;

                foreach (var item in selected)
                {
                    if (token.IsCancellationRequested) break;

                    // Минимальный объект App для сервиса
                    var app = new Ven4Tools.Models.App
                    {
                        Id          = item.Id,
                        Name        = item.DisplayName.Split('[')[0].Trim().TrimEnd(' ', '✅').Trim(),
                        DownloadUrl = item.DownloadUrl,
                        Sha256      = item.Sha256
                    };

                    var progress = new Progress<(string status, int pct)>(v =>
                    {
                        if (v.pct >= 0) progressCache.Value = v.pct;
                        txtCacheLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {v.status}\n");
                        txtCacheLog.ScrollToEnd();
                    });

                    try
                    {
                        bool ok = await OfflineService.CacheInstallerDirectAsync(app, http, progress, token);
                        if (!ok) errors++;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        txtCacheLog.AppendText($"❌ {app.Name}: {ex.Message}\n");
                        errors++;
                    }

                    done++;
                    progressCache.Value = (double)done / total * 100;
                }

                string summary = token.IsCancellationRequested
                    ? $"⏹ Остановлено. Скачано: {done}/{total}"
                    : $"✅ Готово: {done}/{total}{(errors > 0 ? $", ошибок: {errors}" : "")}";
                txtCacheLog.AppendText($"\n{summary}\n");
                txtCacheLog.ScrollToEnd();
                AppLogger.Write(summary);
            }
            catch (Exception ex)
            {
                txtCacheLog.AppendText($"❌ Ошибка: {ex.Message}\n");
                AppLogger.Write($"❌ Ошибка кэширования: {ex.Message}");
            }
            finally
            {
                btnDownloadToCache.IsEnabled      = true;
                btnCancelCacheDownload.Visibility = Visibility.Collapsed;
                btnCancelCacheDownload.IsEnabled  = true;
                progressCache.Value = 0;
                UpdateCacheStats();
                LoadCacheAppsList();

                _cacheCts.Dispose();
                _cacheCts = null;
            }
        }

        private void BtnCancelCacheDownload_Click(object sender, RoutedEventArgs e)
        {
            _cacheCts?.Cancel();
            btnCancelCacheDownload.IsEnabled = false;
        }
    }
}
