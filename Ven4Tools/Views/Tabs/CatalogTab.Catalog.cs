using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private static readonly Dictionary<AppCategory, string> CategoryNames = new()
        {
            [AppCategory.Браузеры]         = "Браузеры",
            [AppCategory.Офис]             = "Офис",
            [AppCategory.Графика]          = "Графика",
            [AppCategory.Разработка]       = "Разработка",
            [AppCategory.Мессенджеры]      = "Мессенджеры",
            [AppCategory.Мультимедиа]      = "Мультимедиа",
            [AppCategory.Системные]        = "Системные",
            [AppCategory.ИгровыеСервисы]   = "Игровые сервисы",
            [AppCategory.Драйверпаки]      = "Драйверпаки",
            [AppCategory.Другое]           = "Другое",
            [AppCategory.Пользовательские] = "Пользовательские",
        };

        private async Task LoadCatalogAndRefreshAsync()
        {
            ShowLoading();
            try
            {
                if (_catalogLoader == null) return;

                // Fast-path: splash already preloaded the catalog
                if (CatalogLoaderService.LoadedCatalog != null)
                {
                    _catalog = CatalogLoaderService.LoadedCatalog;
                    SyncCatalogToAppManager();
                    appManager.LoadAlternativeSources();

                    string preloadedSource = _catalog.Source switch
                    {
                        "online"   => "🌐 Каталог загружен из интернета",
                        "cache"    => "💾 Каталог из кэша (интернет недоступен)",
                        "embedded" => "📀 Встроенный каталог (минимальный набор)",
                        _          => "❓ Неизвестный источник"
                    };
                    AddLog(preloadedSource);
                    AddLog($"📦 Каталог предзагружен: {_catalog.Apps.Count} приложений");

                    LoadApps();

                    if (UserSession.IsLoggedIn)
                        await SyncUserAppsFromServerAsync();

                    return;
                }

                var loadedCatalog = await _catalogLoader.LoadCatalogAsync();

                if (loadedCatalog == null)
                {
                    AddLog("❌ Ошибка: не удалось загрузить каталог");
                    return;
                }

                _catalog = loadedCatalog;
                SyncCatalogToAppManager();
                appManager.LoadAlternativeSources();

                string sourceText = _catalog.Source switch
                {
                    "online"   => "🌐 Каталог загружен из интернета",
                    "cache"    => "💾 Каталог из кэша (интернет недоступен)",
                    "embedded" => "📀 Встроенный каталог (минимальный набор)",
                    _          => "❓ Неизвестный источник"
                };

                AddLog(sourceText);
                AddLog($"Загружено приложений: {_catalog.Apps.Count}");

                LoadApps();

                if (UserSession.IsLoggedIn)
                    await SyncUserAppsFromServerAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка загрузки каталога: {ex.Message}");
            }
            finally
            {
                HideLoading();
            }
        }

        private async void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🔄 Обновление каталога с GitHub...");
            try
            {
                var url = "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";
                AddLog($"📡 URL: {url}");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AppSettings.CatalogTimeout));
                var response = await _httpClient.GetAsync(url, cts.Token);
                AddLog($"📡 HTTP статус: {response.StatusCode}");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    AddLog($"📦 Размер JSON: {json.Length} байт");
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(json, @"""lastUpdated"":\s*""([^""]+)""");
                    if (dateMatch.Success) AddLog($"📅 Версия на GitHub: {dateMatch.Groups[1].Value}");
                }
                var catalogCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "master.json");
                try { if (File.Exists(catalogCachePath)) File.Delete(catalogCachePath); } catch { }
                if (_catalogLoader == null) { AddLog("❌ Ошибка: загрузчик каталога не инициализирован"); return; }
                var loadedCatalog = await _catalogLoader.LoadCatalogAsync();
                if (loadedCatalog == null) { AddLog("❌ Ошибка: не удалось загрузить каталог"); return; }
                _catalog = loadedCatalog;
                SyncCatalogToAppManager();
                appManager.LoadAlternativeSources();
                appManager.ApplyAlternativesToCatalog(_catalog);
                AddLog($"📦 Загружено приложений: {_catalog.Apps.Count}");
                LoadApps();
                AddLog("✅ Каталог успешно обновлён");
            }
            catch (Exception ex) { AddLog($"❌ Ошибка: {ex.Message}"); }
        }

        private void SyncCatalogToAppManager()
        {
            if (_catalog == null) return;
            foreach (var catalogApp in _catalog.Apps)
            {
                var existing = appManager.GetAppById(catalogApp.Id);
                if (existing == null)
                {
                    var appInfo = new AppInfo
                    {
                        Id = catalogApp.Id,
                        DisplayName = catalogApp.Name,
                        Category = GetCategoryFromString(catalogApp.Category),
                        InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                            ? new List<string> { catalogApp.DownloadUrl }
                            : new List<string>(),
                        AlternativeId = catalogApp.WingetId,
                        RequiredSpaceMB = ParseSizeToMB(catalogApp.Size),
                        IsUserAdded = false,
                        ChocoId = catalogApp.ChocoId,
                        ScoopId = catalogApp.ScoopId
                    };
                    appManager.AddCatalogApp(appInfo);
                }
                else if (!existing.IsUserAdded)
                {
                    if (!string.IsNullOrEmpty(catalogApp.DownloadUrl))
                        existing.InstallerUrls = new List<string> { catalogApp.DownloadUrl };
                    if (!string.IsNullOrEmpty(catalogApp.WingetId))
                        existing.AlternativeId = catalogApp.WingetId;
                }
            }
        }

        // Маппинг вынесен в общий хелпер — используется и пользовательскими приложениями
        private static AppCategory GetCategoryFromString(string category) =>
            AppCategoryHelper.Parse(category);

        private int ParseSizeToMB(string size)
        {
            if (string.IsNullOrEmpty(size)) return 100;
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(size, @"(\d+(?:\.\d+)?)");
                if (match.Success && double.TryParse(match.Value, out double value))
                {
                    if (size.Contains("GB", StringComparison.OrdinalIgnoreCase)) return (int)(value * 1024);
                    return (int)value;
                }
            }
            catch { }
            return 100;
        }

        private void ApplyCategorySourceHeaders()
        {
            bool perCategory = SourceOrderService.Current.Mode == "per_category";

            foreach (var (category, panel) in categoryPanels)
            {
                if (panel.Parent is not System.Windows.Controls.Expander expander) continue;
                if (!CategoryNames.TryGetValue(category, out var catName)) continue;

                if (!perCategory)
                {
                    expander.Header = GetOriginalExpanderHeader(category);
                    continue;
                }

                var grid = new System.Windows.Controls.Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new System.Windows.Controls.TextBlock
                {
                    Text = GetOriginalExpanderHeader(category),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                    FontWeight = FontWeights.SemiBold,
                    FontSize   = 13
                };
                Grid.SetColumn(label, 0);

                var combo = new System.Windows.Controls.ComboBox
                {
                    Width   = 160,
                    Height  = 24,
                    FontSize = 11,
                    Margin  = new Thickness(8, 0, 4, 0),
                    Tag     = catName
                };

                combo.Items.Add(new ComboBoxItem { Content = "🔀 Глобальный", Tag = "" });
                foreach (var srcId in SourceOrderSettings.AllSources)
                    combo.Items.Add(new ComboBoxItem { Content = SourceOrderSettings.Labels[srcId], Tag = srcId });

                string current = SourceOrderService.GetCategoryPrimary(catName);
                combo.SelectedIndex = string.IsNullOrEmpty(current)
                    ? 0
                    : SourceOrderSettings.AllSources.IndexOf(current) + 1;

                combo.SelectionChanged += (s, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem item)
                    {
                        string src = item.Tag?.ToString() ?? "";
                        SourceOrderService.SetCategoryPrimary(catName, src);
                        SourceOrderService.Save();
                    }
                };

                Grid.SetColumn(combo, 1);
                grid.Children.Add(label);
                grid.Children.Add(combo);
                expander.Header = grid;
            }
        }

        private static string GetOriginalExpanderHeader(AppCategory cat) => cat switch
        {
            AppCategory.Браузеры         => "🌐 Браузеры",
            AppCategory.Офис             => "📁 Офис",
            AppCategory.Графика          => "🎨 Графика",
            AppCategory.Разработка       => "💻 Разработка",
            AppCategory.Мессенджеры      => "💬 Мессенджеры",
            AppCategory.Мультимедиа      => "🎵 Мультимедиа",
            AppCategory.Системные        => "⚙️ Системные",
            AppCategory.ИгровыеСервисы   => "🎮 Игровые сервисы",
            AppCategory.Драйверпаки      => "🖨️ Драйверпаки",
            AppCategory.Другое           => "📎 Другое",
            AppCategory.Пользовательские => "👤 Пользовательские",
            _                            => cat.ToString()
        };

        private void OnSourceOrderChanged()
        {
            Dispatcher.Invoke(() =>
            {
                ApplyCategorySourceHeaders();
                RefreshAvailability_Click(this, new RoutedEventArgs());
            });
        }
    }
}
