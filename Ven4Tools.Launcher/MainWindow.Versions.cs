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
                // Предикат/маппинг общие с GitHubService — см. IsClientZipAsset/MapRelease.
                var firstStable = GitHubService.FindFirstStableClientRelease(releases);
                foreach (var release in releases)
                {
                    var version = release.tag_name?.TrimStart('v');
                    if (string.IsNullOrEmpty(version)) continue;

                    var clientAsset = GitHubService.FindClientZipAsset(release);
                    if (clientAsset != null)
                    {
                        // Качаем только с доверенных доменов GitHub по HTTPS
                        if (!DownloadValidator.IsAllowedDownloadHost(clientAsset.browser_download_url))
                        {
                            AddLog($"   ⛔ {version} — недоверенный хост загрузки, релиз пропущен: {clientAsset.browser_download_url}");
                            continue;
                        }

                        // Базовый маппинг (GitHub-ссылка) — общий с автообновлением;
                        // CDN-подстановку накладываем поверх только в этом ручном пути.
                        var info = GitHubService.MapRelease(release, firstStable)!;
                        string githubUrl = clientAsset.browser_download_url ?? "";

                        // Если CDN знает эту версию — качаем с CDN (быстрее),
                        // GitHub оставляем как резерв на случай недоступности CDN.
                        if (cdnInfo?.Client != null &&
                            string.Equals(cdnInfo.Client.Version, version, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(cdnInfo.Client.ZipUrl) &&
                            DownloadValidator.IsAllowedDownloadHost(cdnInfo.Client.ZipUrl))
                        {
                            info.DownloadUrl = cdnInfo.Client.ZipUrl!;
                            info.FallbackUrl = cdnInfo.Client.ZipFallback ?? githubUrl;
                            // Хеш из version.json относится к одному и тому же zip
                            // (CDN и GitHub отдают идентичный архив), поэтому годится
                            // и для основной, и для резервной ссылки.
                            info.ExpectedSha256 = cdnInfo.Client.ZipSha256;
                            AddLog($"   ⚡ {version} → CDN (резерв: GitHub)");
                        }

                        AddLog($"   ✅ {version}{(release.prerelease ? " [PRE]" : "")} → {clientAsset.name}");
                        _availableVersions.Add(info);
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
                    UpdateVersionDisplay(_availableVersions.FirstOrDefault(v => v.IsLatest));
                    AddLog($"✅ Загружено {_availableVersions.Count} версий");
                    CheckExistingClient();
                    CheckClientUpdateAvailable();
                }
                else
                {
                    UpdateVersionDisplay(null);
                    AddLog("⚠️ Нет релизов с подходящим .zip-активом (см. детали выше)");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка загрузки версий: {ex.Message}");
            }
        }

        // Три состояния кнопки запуска/загрузки клиента с фиксированными текстом и
        // цветом — единый источник вместо повторяющихся хардкод-RGB в четырёх местах.
        private enum LaunchButtonState { Launch, Download, Update }

        private static readonly System.Windows.Media.Brush LaunchBrush   = CreateFrozenBrush(0, 120, 212);   // синий
        private static readonly System.Windows.Media.Brush DownloadBrush = CreateFrozenBrush(255, 140, 0);   // оранжевый
        private static readonly System.Windows.Media.Brush UpdateBrush   = CreateFrozenBrush(251, 191, 36);  // янтарный

        private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze(); // общая иммутабельная кисть, безопасна для повторного использования
            return brush;
        }

        private void SetLaunchButtonState(LaunchButtonState state)
        {
            (btnLaunchApp.Content, btnLaunchApp.Background) = state switch
            {
                LaunchButtonState.Launch   => ("🚀 Запустить Ven4Tools", LaunchBrush),
                LaunchButtonState.Download => ("📥 Загрузить Ven4Tools", DownloadBrush),
                LaunchButtonState.Update   => ("⬆ Обновить Ven4Tools",   UpdateBrush),
                _                          => (btnLaunchApp.Content, btnLaunchApp.Background)
            };
        }

        private void CheckExistingClient()
        {
            string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
            if (File.Exists(clientExe))
            {
                var versionInfo    = FileVersionInfo.GetVersionInfo(clientExe);
                string currentVersion = versionInfo.FileVersion ?? "unknown";
                txtInstalledVersion.Text = $"Текущая версия: {currentVersion}";
                SetLaunchButtonState(LaunchButtonState.Launch);
                AddLog($"✅ Найден клиент версии {currentVersion}");
            }
            else
            {
                txtInstalledVersion.Text = "Текущая версия: не установлена";
                SetLaunchButtonState(LaunchButtonState.Download);
            }
        }

        // Сравнивает установленную версию клиента с последней доступной и переключает
        // btnLaunchApp в состояние «Обновить», если найдена более новая версия.
        // Вызывается после LoadVersionsAsync — общий путь и для ручной проверки
        // («Проверить обновления»), и для авто-обновления (Task 6).
        private void CheckClientUpdateAvailable()
        {
            string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
            if (!File.Exists(clientExe)) { _clientUpdateAvailable = false; return; }

            string installedVersion = FileVersionInfo.GetVersionInfo(clientExe).FileVersion ?? "0.0.0";
            var latest = _availableVersions.FirstOrDefault(v => v.IsLatest);
            if (latest == null || !VersionComparer.IsNewer(latest.Version, installedVersion))
            {
                _clientUpdateAvailable = false;
                return;
            }

            _clientUpdateAvailable  = true;
            UpdateVersionDisplay(latest);
            SetLaunchButtonState(LaunchButtonState.Update);
            AddLog($"📢 Доступно обновление клиента: {installedVersion} → {latest.Version}");
        }

        // Единственная доступная (актуальная) версия клиента — старые релизы больше
        // не выбираются вручную (у них нет zip-ассета, см. prune-old-client-assets.yml):
        // версия и дата релиза показываются как статичная информация, а не как выбор
        // из списка. Не путать с txtInstalledVersion (CheckExistingClient) — это то,
        // что реально стоит на диске у пользователя, а не то, что доступно на сервере.
        private void UpdateVersionDisplay(ClientVersionInfo? version)
        {
            _selectedVersion = version;

            if (version == null)
            {
                txtClientVersion.Text = "Актуальная версия: —";
                txtVersionInfo.Text   = "Нет доступной версии";
                return;
            }

            txtClientVersion.Text = $"Актуальная версия: {version.Version}";
            txtVersionInfo.Text = version.FileSize > 0
                ? $"Релиз от {version.ReleaseDate:dd.MM.yyyy}  ·  {version.FileSize / 1024 / 1024} МБ"
                : version.ReleaseDate != default
                    ? $"Релиз от {version.ReleaseDate:dd.MM.yyyy}"
                    : "Информация о релизе недоступна";

            if (_detailsPanelOpen)
                ShowReleaseNotes(version.ReleaseNotes);
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
