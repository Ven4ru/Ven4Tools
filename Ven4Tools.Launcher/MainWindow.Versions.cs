using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private async Task LoadVersionsAsync()
        {
            try
            {
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
                var firstStable = releases.FirstOrDefault(r => !r.prerelease);
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
                        AddLog($"   ✅ {version}{(release.prerelease ? " [PRE]" : "")} → {clientAsset.name}");
                        _availableVersions.Add(new ClientVersionInfo
                        {
                            Version      = version,
                            DownloadUrl  = clientAsset.browser_download_url ?? "",
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
