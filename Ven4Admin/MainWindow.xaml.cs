using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ven4Admin
{
    public class CatalogItem : INotifyPropertyChanged
    {
        private bool   _ruBlocked;
        private string _localStatus  = "⬜";
        private string _remoteStatus = "⬜";
        private bool   _isSelected;

        public string  Id          { get; set; } = Guid.NewGuid().ToString();
        public string  Name        { get; set; } = "";
        public string  Category    { get; set; } = "Другое";
        public string  WingetId    { get; set; } = "";
        public string  DownloadUrl { get; set; } = "";
        public string  Version     { get; set; } = "";
        public string  Size        { get; set; } = "";
        public bool    Official    { get; set; } = true;
        public bool    SkipHash    { get; set; }
        public string? Sha256      { get; set; }
        public string  IconUrl     { get; set; } = "";
        public string  Description { get; set; } = "";

        public bool RuBlocked
        {
            get => _ruBlocked;
            set { _ruBlocked = value; OnPC(); }
        }

        public string LocalStatus
        {
            get => _localStatus;
            set { _localStatus = value; OnPC(); }
        }

        public string RemoteStatus
        {
            get => _remoteStatus;
            set { _remoteStatus = value; OnPC(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPC(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public partial class MainWindow : Window
    {
        private const string CatalogUrl    = "https://raw.githubusercontent.com/Ven4ru/Ven4Tools/main/Catalog/master.json";
        private const string DefaultRepo   = @"C:\Users\Vench\Documents\GitHub\Ven4Tools";

        private readonly ObservableCollection<CatalogItem> _items = new();
        private JObject? _originalCatalog;
        private CancellationTokenSource? _scanCts;
        private bool _catalogLoaded;

        public MainWindow()
        {
            InitializeComponent();
            dgApps.ItemsSource = _items;

            txtSshHost.Text = "78.17.116.90";
            txtSshUser.Text = "root";

            btnLoad.Click          += async (_, _) => await LoadFromGitHubAsync();
            btnSave.Click          += (_, _) => SaveToRepo();
            btnPush.Click          += (_, _) => PushToGit();
            btnAddApp.Click        += (_, _) => AddApp();
            btnEdit.Click          += (_, _) => EditSelected();
            btnDelete.Click        += (_, _) => DeleteSelected();
            btnScanAll.Click       += async (_, _) => await ScanAsync(all: true);
            btnScanSelected.Click  += async (_, _) => await ScanAsync(all: false);
            btnAutoMark.Click      += (_, _) => AutoMarkRuBlocked();
            btnDeleteBlocked.Click += (_, _) => DeleteRuBlocked();

            UpdateStats();
            SetStatus("Нажмите «Загрузить с GitHub» для начала работы");
        }

        // ── Загрузка ─────────────────────────────────────────────────────────

        private async Task LoadFromGitHubAsync()
        {
            SetStatus("⏳ Загрузка с GitHub...");
            btnLoad.IsEnabled = false;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "Ven4Admin/1.0");
                var json = await client.GetStringAsync(CatalogUrl);
                ParseCatalog(json);
                txtFilePath.Text = $"GitHub  ·  {_items.Count} приложений";
                SetStatus($"✅ Загружено {_items.Count} приложений");
                SetCatalogLoaded(true);
            }
            catch (Exception ex)
            {
                SetStatus($"❌ Ошибка загрузки: {ex.Message}");
            }
            finally
            {
                btnLoad.IsEnabled = true;
            }
        }

        private void ParseCatalog(string json)
        {
            _originalCatalog = JObject.Parse(json);
            _items.Clear();

            var apps = _originalCatalog["apps"] as JArray ?? new JArray();
            foreach (JObject a in apps)
            {
                _items.Add(new CatalogItem
                {
                    Id          = a["id"]?.ToString()           ?? Guid.NewGuid().ToString(),
                    Name        = a["name"]?.ToString()         ?? "",
                    Category    = a["category"]?.ToString()     ?? "Другое",
                    WingetId    = a["wingetId"]?.ToString()     ?? "",
                    DownloadUrl = a["downloadUrl"]?.ToString()  ?? "",
                    Version     = a["version"]?.ToString()      ?? "",
                    Size        = a["size"]?.ToString()         ?? "",
                    Official    = a["official"]?.Value<bool>()  ?? true,
                    SkipHash    = a["skipHash"]?.Value<bool>()  ?? false,
                    Sha256      = a["sha256"]?.ToString(),
                    IconUrl     = a["iconUrl"]?.ToString()      ?? "",
                    Description = a["description"]?.ToString()  ?? "",
                    RuBlocked   = a["ruBlocked"]?.Value<bool>() ?? false,
                });
            }
            UpdateStats();
        }

        // ── Сохранение и push ─────────────────────────────────────────────────

        private void SaveToRepo()
        {
            if (_originalCatalog == null) { SetStatus("❌ Каталог не загружен"); return; }
            try
            {
                var catalog = (JObject)_originalCatalog.DeepClone();
                catalog["lastUpdated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                catalog["apps"] = new JArray(_items.Select(ToJObject));

                var path = System.IO.Path.Combine(DefaultRepo, "Catalog", "master.json");
                System.IO.File.WriteAllText(path, catalog.ToString(Formatting.Indented), Encoding.UTF8);
                SetStatus($"✅ Сохранено → {path}");
            }
            catch (Exception ex) { SetStatus($"❌ Ошибка сохранения: {ex.Message}"); }
        }

        private void PushToGit()
        {
            try
            {
                SaveToRepo();
                RunGit("add Catalog/master.json");
                RunGit($"commit -m \"Admin: update catalog [{DateTime.Now:yyyy-MM-dd HH:mm}]\"");
                var pushOut = RunGit("push");
                SetStatus("✅ Git push выполнен" + (pushOut.Trim().Length > 0 ? $": {pushOut.Split('\n')[0]}" : ""));
            }
            catch (Exception ex) { SetStatus($"❌ Git ошибка: {ex.Message}"); }
        }

        private string RunGit(string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory       = DefaultRepo,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var p = Process.Start(psi) ?? throw new Exception("git не найден");
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return output;
        }

        private static JObject ToJObject(CatalogItem i) => new()
        {
            ["id"]          = i.Id,
            ["name"]        = i.Name,
            ["category"]    = i.Category,
            ["wingetId"]    = i.WingetId,
            ["downloadUrl"] = i.DownloadUrl,
            ["version"]     = i.Version,
            ["size"]        = i.Size,
            ["official"]    = i.Official,
            ["iconUrl"]     = i.IconUrl,
            ["description"] = i.Description,
            ["sha256"]      = i.Sha256 != null ? (JToken)i.Sha256 : JValue.CreateNull(),
            ["skipHash"]    = i.SkipHash,
            ["ruBlocked"]   = i.RuBlocked,
        };

        // ── Сканирование ──────────────────────────────────────────────────────

        private async Task ScanAsync(bool all)
        {
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            var toScan = all
                ? _items.ToList()
                : _items.Where(i => i.IsSelected).ToList();

            if (toScan.Count == 0)
            {
                SetStatus("⚠ Отметьте приложения галочкой или используйте «Сканировать все»");
                return;
            }

            SetScanUI(false);
            pbScan.Value = 0;
            txtScanProgress.Text = $"0/{toScan.Count}";

            string sshHost = txtSshHost.Text.Trim();
            string sshUser = txtSshUser.Text.Trim();
            string sshPass = pbSshPass.Password;
            bool   hasSsh  = !string.IsNullOrEmpty(sshHost) && !string.IsNullOrEmpty(sshUser);

            int done = 0;
            var sem  = new SemaphoreSlim(3);

            var tasks = toScan.Select(async item =>
            {
                await sem.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    // Локальная проверка (из РФ)
                    item.LocalStatus = "⏳";
                    var local = await CheckLocalAsync(item.DownloadUrl, item.SkipHash, token);
                    item.LocalStatus = local;

                    // SSH-проверка (вне РФ) — только если локально недоступно
                    if (hasSsh)
                    {
                        if (local.StartsWith("❌"))
                        {
                            item.RemoteStatus = "⏳";
                            var remote = await CheckSshAsync(item.DownloadUrl, sshHost, sshUser, sshPass, token);
                            item.RemoteStatus = remote;
                        }
                        else
                        {
                            item.RemoteStatus = "✅ доступно";
                        }
                    }

                    int d = Interlocked.Increment(ref done);
                    Dispatcher.Invoke(() =>
                    {
                        pbScan.Value         = (double)d / toScan.Count * 100;
                        txtScanProgress.Text = $"{d}/{toScan.Count}";
                    });
                }
                finally { sem.Release(); }
            });

            await Task.WhenAll(tasks);

            SetScanUI(true);
            int localBlocked  = _items.Count(i => i.LocalStatus.StartsWith("❌"));
            int remoteBlocked = _items.Count(i => i.RemoteStatus.StartsWith("❌"));
            SetStatus($"✅ Сканирование завершено. Недоступно в РФ: {localBlocked}  |  Недоступно вне РФ: {remoteBlocked}");
        }

        private static async Task<string> CheckLocalAsync(string url, bool skipHash, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(url)) return "⬜ нет URL";
            if (skipHash)                        return "⬜ skipHash";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                var req  = new HttpRequestMessage(HttpMethod.Head, url);
                var resp = await client.SendAsync(req, token);
                int code = (int)resp.StatusCode;
                return code < 400 ? $"✅ {code}" : $"❌ {code}";
            }
            catch (TaskCanceledException)         { return "❌ таймаут"; }
            catch (HttpRequestException ex)
            {
                return ex.StatusCode.HasValue
                    ? $"❌ {(int)ex.StatusCode.Value}"
                    : "❌ нет связи";
            }
            catch { return "❌ ошибка"; }
        }

        private static async Task<string> CheckSshAsync(
            string url, string host, string user, string pass, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(url)) return "⬜";

            // Per-check timeout: 20 s total (SSH connect + curl --max-time 10)
            using var perCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, perCheckCts.Token);
            var ct = linked.Token;

            Process? p = null;
            try
            {
                string safeUrl = url.Replace("'", "'\\''");
                var psi = new ProcessStartInfo("plink",
                    $"-pw \"{pass}\" -noagent {user}@{host} " +
                    $"\"curl -sI -L --max-time 10 '{safeUrl}' 2>/dev/null | head -1\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    RedirectStandardInput  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                p = Process.Start(psi);
                if (p == null) return "⬜ plink не найден";

                p.StandardInput.WriteLine("y");
                p.StandardInput.Flush();

                // Read both streams in parallel to prevent deadlock
                using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
                var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = p.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(stdoutTask, stderrTask);

                string combined = (stdoutTask.Result + stderrTask.Result).Trim();
                if (combined.Contains("200") || combined.Contains("301") || combined.Contains("302") ||
                    combined.Contains("303") || combined.Contains("307") || combined.Contains("308"))
                    return "✅ доступно";

                if (string.IsNullOrWhiteSpace(combined)) return "⬜ нет ответа";
                var firstLine = combined.Split('\n')
                    .FirstOrDefault(l => !l.Contains("WARNING") && !l.Contains("key") && l.Trim().Length > 0)
                    ?? combined.Split('\n')[0];
                return $"❌ {firstLine.Trim().Truncate(30)}";
            }
            catch (OperationCanceledException) { return "❌ таймаут SSH"; }
            catch (System.ComponentModel.Win32Exception) { return "⬜ plink не найден"; }
            catch (Exception ex) { return $"⬜ {ex.Message.Truncate(25)}"; }
            finally { try { p?.Kill(); } catch { } }
        }

        // ── Действия ─────────────────────────────────────────────────────────

        private void AutoMarkRuBlocked()
        {
            int marked = 0, cleared = 0;
            foreach (var item in _items)
            {
                if (item.LocalStatus.StartsWith("❌") && item.RemoteStatus.StartsWith("✅"))
                {
                    if (!item.RuBlocked) { item.RuBlocked = true; marked++; }
                }
                else if (item.LocalStatus.StartsWith("✅"))
                {
                    if (item.RuBlocked) { item.RuBlocked = false; cleared++; }
                }
            }
            UpdateStats();
            SetStatus($"⚡ Авто-пометка завершена. Помечено: +{marked}  Снято: -{cleared}");
        }

        private void DeleteRuBlocked()
        {
            var blocked = _items.Where(i => i.RuBlocked).ToList();
            if (blocked.Count == 0)
            {
                SetStatus("ℹ Нет приложений с ruBlocked = true");
                return;
            }

            string list = string.Join("\n",
                blocked.Take(12).Select(a => $"  • {a.Name}"));
            if (blocked.Count > 12)
                list += $"\n  ... и ещё {blocked.Count - 12}";

            var res = MessageBox.Show(
                $"Удалить {blocked.Count} приложений с пометкой ruBlocked?\n\n{list}",
                "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            foreach (var item in blocked)
                _items.Remove(item);

            UpdateStats();
            SetStatus($"🗑 Удалено {blocked.Count} приложений с ruBlocked");
        }

        private void AddApp()
        {
            var dlg = new AppEditWindow(null) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _items.Insert(0, dlg.Result);
                UpdateStats();
                SetStatus($"➕ Добавлено: {dlg.Result.Name}");
            }
        }

        private void EditSelected()
        {
            if (dgApps.SelectedItem is not CatalogItem item) return;
            var dlg = new AppEditWindow(item) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;

            var r = dlg.Result;
            item.Name        = r.Name;
            item.Category    = r.Category;
            item.WingetId    = r.WingetId;
            item.DownloadUrl = r.DownloadUrl;
            item.Version     = r.Version;
            item.Size        = r.Size;
            item.Description = r.Description;
            item.Official    = r.Official;
            item.RuBlocked   = r.RuBlocked;
            item.SkipHash    = r.SkipHash;
            item.IconUrl     = r.IconUrl;
            SetStatus($"✏ Обновлено: {item.Name}");
        }

        private void DeleteSelected()
        {
            var selected = _items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0 && dgApps.SelectedItem is CatalogItem sel)
                selected.Add(sel);
            if (selected.Count == 0) { SetStatus("⚠ Нет выбранных приложений"); return; }

            var res = MessageBox.Show(
                $"Удалить {selected.Count} приложений?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            foreach (var item in selected)
                _items.Remove(item);

            UpdateStats();
            SetStatus($"🗑 Удалено: {selected.Count}");
        }

        // ── Вспомогательные ──────────────────────────────────────────────────

        private void UpdateStats()
        {
            int total   = _items.Count;
            int blocked = _items.Count(i => i.RuBlocked);
            int noUrl   = _items.Count(i => string.IsNullOrEmpty(i.DownloadUrl) && !i.SkipHash);
            int winget  = _items.Count(i => !string.IsNullOrEmpty(i.WingetId));
            txtStats.Text = $"Всего: {total}\nruBlocked: {blocked}\nС WinGet: {winget}\nБез URL: {noUrl}";
        }

        private void SetStatus(string msg) =>
            Dispatcher.Invoke(() => txtStatus.Text = $"[{DateTime.Now:HH:mm:ss}]  {msg}");

        private void SetScanUI(bool enabled) =>
            Dispatcher.Invoke(() =>
            {
                btnScanAll.IsEnabled      = enabled;
                btnScanSelected.IsEnabled = enabled;
            });

        private void SetCatalogLoaded(bool loaded)
        {
            _catalogLoaded = loaded;
            btnSave.IsEnabled          = loaded;
            btnPush.IsEnabled          = loaded;
            btnAddApp.IsEnabled        = loaded;
            btnEdit.IsEnabled          = loaded;
            btnDelete.IsEnabled        = loaded;
            btnScanAll.IsEnabled       = loaded;
            btnScanSelected.IsEnabled  = loaded;
            btnAutoMark.IsEnabled      = loaded;
            btnDeleteBlocked.IsEnabled = loaded;
        }
    }

    internal static class StringExt
    {
        public static string Truncate(this string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }
}
