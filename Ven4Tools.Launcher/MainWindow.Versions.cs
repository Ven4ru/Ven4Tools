using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private async Task LoadVersionsAsync()
        {
            try
            {
                // CDN — основной источник ссылки на текущую версию (быстрее GitHub).
                // Список всех версий по-прежнему берём из GitHub-релизов; для версии,
                // совпадающей с CDN, основной ссылкой ставим CDN, а GitHub — резервом.
                CdnVersionInfo? cdnInfo = null;
                try
                {
                    using var cdnService = new CdnService();
                    cdnInfo = await cdnService.GetVersionInfoAsync();
                    if (cdnInfo?.Client?.ZipUrl != null)
                        AddLog($"🌐 CDN доступен: клиент {cdnInfo.Client.Version}");
                    else
                        AddLog("⚠️ CDN недоступен, использую GitHub как основной источник");
                }
                catch { AddLog("⚠️ CDN недоступен, использую GitHub как основной источник"); }

                AddLog("🔍 Загрузка списка версий с GitHub...");
                using var gitHubService = new GitHubService();

                var (releases, error) = await gitHubService.GetAllReleasesWithError();

                if (error != null)
                {
                    AddLog($"❌ {error}");
                    return;
                }

                AddLog($"📦 Найдено релизов: {releases.Count}");

                _availableVersions = new System.Collections.Generic.List<ClientVersionInfo>();
                // «latest» — первый стабильный релиз именно с клиентским zip-архивом;
                // launcher-only релизы (без Client-*.zip) не должны помечаться как latest.
                var firstStable = releases.FirstOrDefault(r =>
                    !r.prerelease &&
                    r.assets?.Any(a =>
                        a.name != null &&
                        (a.name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                         a.name.Contains("Ven4Tools", StringComparison.OrdinalIgnoreCase)) &&
                        a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        !a.name.Contains("Launcher", StringComparison.OrdinalIgnoreCase)) == true);
                foreach (var release in releases)
                {
                    var version = release.tag_name?.TrimStart('v');
                    if (string.IsNullOrEmpty(version)) continue;

                    var clientAsset = release.assets?.FirstOrDefault(a =>
                        a.name != null &&
                        (a.name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                         a.name.Contains("Ven4Tools", StringComparison.OrdinalIgnoreCase)) &&
                        a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        !a.name.Contains("Launcher", StringComparison.OrdinalIgnoreCase));

                    if (clientAsset != null)
                    {
                        // Качаем только с доверенных доменов GitHub по HTTPS
                        if (!DownloadValidator.IsAllowedDownloadHost(clientAsset.browser_download_url))
                        {
                            AddLog($"   ⛔ {version} — недоверенный хост загрузки, релиз пропущен: {clientAsset.browser_download_url}");
                            continue;
                        }

                        string githubUrl   = clientAsset.browser_download_url ?? "";
                        string downloadUrl = githubUrl;
                        string? fallbackUrl = null;
                        string? expectedSha256 = null;

                        // Если CDN знает эту версию — качаем с CDN (быстрее),
                        // GitHub оставляем как резерв на случай недоступности CDN.
                        if (cdnInfo?.Client != null &&
                            string.Equals(cdnInfo.Client.Version, version, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(cdnInfo.Client.ZipUrl) &&
                            DownloadValidator.IsAllowedDownloadHost(cdnInfo.Client.ZipUrl))
                        {
                            downloadUrl = cdnInfo.Client.ZipUrl!;
                            fallbackUrl = cdnInfo.Client.ZipFallback ?? githubUrl;
                            // Хеш из version.json относится к одному и тому же zip
                            // (CDN и GitHub отдают идентичный архив), поэтому годится
                            // и для основной, и для резервной ссылки.
                            expectedSha256 = cdnInfo.Client.ZipSha256;
                            AddLog($"   ⚡ {version} → CDN (резерв: GitHub)");
                        }

                        AddLog($"   ✅ {version}{(release.prerelease ? " [PRE]" : "")} → {clientAsset.name}");
                        _availableVersions.Add(new ClientVersionInfo
                        {
                            Version      = version,
                            DownloadUrl  = downloadUrl,
                            FallbackUrl  = fallbackUrl,
                            ExpectedSha256 = expectedSha256,
                            ReleaseDate  = release.published_at,
                            ReleaseNotes = release.body,
                            IsPreRelease = release.prerelease,
                            IsLatest     = release == firstStable,
                            FileSize     = clientAsset.size
                        });
                    }
                    else
                    {
                        var assetNames = release.assets != null
                            ? string.Join(", ", release.assets.Select(a => a.name))
                            : "нет";
                        AddLog($"   ⚠️ {version} — нет подходящего .zip (assets: {assetNames})");
                    }
                }

                _availableVersions.Sort((a, b) => VersionComparer.Compare(b.Version, a.Version));

                if (_availableVersions.Any())
                {
                    cmbVersions.ItemsSource  = _availableVersions;
                    cmbVersions.SelectedItem = _availableVersions.FirstOrDefault(v => v.IsLatest);
                    cmbVersions.IsEnabled    = true;
                    AddLog($"✅ Загружено {_availableVersions.Count} версий");
                    CheckExistingClient();
                    CheckClientUpdateAvailable();
                }
                else
                {
                    AddLog("⚠️ Нет релизов с подходящим .zip-активом (см. детали выше)");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка загрузки версий: {ex.Message}");
            }
        }

        private void CheckExistingClient()
        {
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
            if (File.Exists(clientExe))
            {
                var versionInfo    = FileVersionInfo.GetVersionInfo(clientExe);
                string currentVersion = versionInfo.FileVersion ?? "unknown";
                btnLaunchApp.Content    = "🚀 Запустить Ven4Tools";
                btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
                AddLog($"✅ Найден клиент версии {currentVersion}");
            }
            else
            {
                btnLaunchApp.Content    = "📥 Загрузить Ven4Tools";
                btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
            }
        }

        // Сравнивает установленную версию клиента с последней доступной и переключает
        // btnLaunchApp в состояние «Обновить», если найдена более новая версия.
        // Вызывается после LoadVersionsAsync — общий путь и для ручной проверки
        // («Проверить обновления»), и для авто-обновления (Task 6).
        private void CheckClientUpdateAvailable()
        {
            string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
            if (!File.Exists(clientExe)) { _clientUpdateAvailable = false; return; }

            string installedVersion = FileVersionInfo.GetVersionInfo(clientExe).FileVersion ?? "0.0.0";
            var latest = _availableVersions.FirstOrDefault(v => v.IsLatest);
            if (latest == null || !VersionComparer.IsNewer(latest.Version, installedVersion))
            {
                _clientUpdateAvailable = false;
                return;
            }

            _clientUpdateAvailable  = true;
            _selectedVersion        = latest;
            cmbVersions.SelectedItem = latest;
            btnLaunchApp.Content    = "⬆ Обновить Ven4Tools";
            btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
            AddLog($"📢 Доступно обновление клиента: {installedVersion} → {latest.Version}");
        }

        private void CmbVersions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbVersions.SelectedItem is ClientVersionInfo version)
            {
                _selectedVersion = version;

                if (version.FileSize > 0)
                    txtVersionInfo.Text = $"{version.ReleaseDate:dd.MM.yyyy}  ·  {version.FileSize / 1024 / 1024} МБ";
                else
                    txtVersionInfo.Text = version.ReleaseDate != default ? $"{version.ReleaseDate:dd.MM.yyyy}" : "Выберите версию";

                if (_detailsPanelOpen)
                    ShowReleaseNotes(version.ReleaseNotes);

                string clientExe = Path.Combine(_clientPath, "Ven4Tools.exe");
                if (File.Exists(clientExe))
                {
                    // Ручной выбор версии возвращает кнопку в режим «Запустить»,
                    // поэтому сбрасываем флаг обновления — иначе клик по кнопке
                    // с надписью «Запустить» ушёл бы в ветку загрузки (Task 5).
                    _clientUpdateAvailable  = false;
                    btnLaunchApp.Content    = "🚀 Запустить Ven4Tools";
                    btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
                }
                else
                {
                    btnLaunchApp.Content    = "📥 Загрузить Ven4Tools";
                    btnLaunchApp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0));
                }

                AddLog($"📌 Выбрана версия: {version.Version}");
            }
        }

        private void ShowReleaseNotes(string? notes)
        {
            fdvReleaseNotes.Document = ParseMarkdown(notes);
        }

        private void BtnChangelog_Click(object sender, RoutedEventArgs e)
        {
            _detailsPanelOpen = !_detailsPanelOpen;
            if (_detailsPanelOpen)
            {
                colDetails.Width = new System.Windows.GridLength(300);
                MotionService.SlideIn(fdvReleaseNotes, 12, 200);
                if (_selectedVersion != null)
                    ShowReleaseNotes(_selectedVersion.ReleaseNotes);
            }
            else
            {
                colDetails.Width = new System.Windows.GridLength(0);
            }
        }

        private void BtnCloseDetails_Click(object sender, RoutedEventArgs e)
        {
            _detailsPanelOpen = false;
            colDetails.Width  = new System.Windows.GridLength(0);
        }

        private System.Windows.Documents.FlowDocument ParseMarkdown(string? markdown)
        {
            var doc = new System.Windows.Documents.FlowDocument
            {
                Background  = System.Windows.Media.Brushes.Transparent,
                Foreground  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                FontFamily  = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize    = 12,
                PagePadding = new Thickness(4)
            };

            if (string.IsNullOrWhiteSpace(markdown))
            {
                doc.Blocks.Add(new System.Windows.Documents.Paragraph(
                    new System.Windows.Documents.Run("Нет описания для этой версии.")
                    { Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 170, 170)) }));
                return doc;
            }

            var accentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212));
            var subBrush    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 200, 255));
            var textBrush   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
            var mutedBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 170, 170));

            System.Windows.Documents.List? currentList = null;

            foreach (var rawLine in markdown.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.StartsWith("## "))
                {
                    currentList = null;
                    var para = new System.Windows.Documents.Paragraph
                    {
                        Margin          = new Thickness(0, 8, 0, 2),
                        BorderBrush     = accentBrush,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding         = new Thickness(0, 0, 0, 2)
                    };
                    para.Inlines.Add(new System.Windows.Documents.Run(line.Substring(3))
                        { FontWeight = FontWeights.Bold, FontSize = 14, Foreground = accentBrush });
                    doc.Blocks.Add(para);
                }
                else if (line.StartsWith("### "))
                {
                    currentList = null;
                    var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 6, 0, 2) };
                    para.Inlines.Add(new System.Windows.Documents.Run(line.Substring(4))
                        { FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = subBrush });
                    doc.Blocks.Add(para);
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    if (currentList == null)
                    {
                        currentList = new System.Windows.Documents.List
                        {
                            MarkerStyle = System.Windows.TextMarkerStyle.Disc,
                            Margin      = new Thickness(16, 2, 0, 2),
                            Padding     = new Thickness(8, 0, 0, 0)
                        };
                        doc.Blocks.Add(currentList);
                    }
                    var item = new System.Windows.Documents.ListItem();
                    var ip   = new System.Windows.Documents.Paragraph { Margin = new Thickness(0) };
                    ip.Inlines.Add(new System.Windows.Documents.Run(line.Substring(2).Trim()) { Foreground = textBrush });
                    item.Blocks.Add(ip);
                    currentList.ListItems.Add(item);
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    currentList = null;
                }
                else
                {
                    currentList = null;
                    var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                    para.Inlines.Add(new System.Windows.Documents.Run(line) { Foreground = mutedBrush });
                    doc.Blocks.Add(para);
                }
            }

            return doc;
        }
    }
}
