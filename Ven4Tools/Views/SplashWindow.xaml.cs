using System;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    public partial class SplashWindow : Window
    {
        private static readonly Uri _loadingUri =
            new Uri("pack://application:,,,/Resources/Mascots/loading.png");
        private static readonly Uri _readyUri =
            new Uri("pack://application:,,,/Resources/Mascots/ready.png");

        private readonly CancellationTokenSource _skipCts = new();

        public SplashWindow()
        {
            InitializeComponent();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            btnSkip.IsEnabled = false;
            _skipCts.Cancel();
        }

        public async Task RunPreloadAsync()
        {
            var ct = _skipCts.Token;
            try
            {
                // 1. Сеть
                SetStatus("Проверка сети...");
                bool online = await CheckNetworkAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (!online)
                {
                    SetStatus("⚠ Сеть недоступна — каталог из кэша");
                    await Task.Delay(900, ct);
                }

                // 2. Каталог
                SetStatus("Загрузка каталога...");
                try { await CatalogLoaderService.PreloadAsync(); } catch { }
                ct.ThrowIfCancellationRequested();

                // 3. Права администратора
                SetStatus("Проверка прав администратора...");
                if (!IsRunningAsAdmin())
                {
                    SetStatus("⚠ Нет прав администратора — winget может не работать");
                    await Task.Delay(1200, ct);
                }
                ct.ThrowIfCancellationRequested();

                // 4. WebView2
                SetStatus("Проверка WebView2...");
                string? wv2 = GetWebView2Version();
                if (wv2 == null)
                {
                    SetStatus("⚠ WebView2 не установлен");
                    await Task.Delay(900, ct);
                }
                ct.ThrowIfCancellationRequested();

                // 5. winget
                SetStatus("Проверка winget...");
                await Task.Run(async () =>
                {
                    try { await CheckWingetAsync(ct); } catch { }
                }, ct);
                ct.ThrowIfCancellationRequested();

                // Готово
                SetImage(_readyUri);
                Dispatcher.Invoke(() => btnSkip.Visibility = Visibility.Collapsed);
                SetStatus("Готово!");
                await Task.Delay(700, ct);
            }
            catch (OperationCanceledException) { /* пользователь нажал «Пропустить» */ }
            catch { /* предзагрузка — best-effort: любая ошибка не должна валить старт */ }
        }

        private void SetStatus(string text) =>
            Dispatcher.Invoke(() => txtStatus.Text = text);

        private void SetImage(Uri uri) =>
            Dispatcher.Invoke(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                imgMascot.Source = bmp;
            });

        private static async Task<bool> CheckNetworkAsync(CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var response = await client.GetAsync(
                    "https://raw.githubusercontent.com",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                return true;
            }
            catch { return false; }
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string? GetWebView2Version()
        {
            try
            {
                return Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .GetAvailableBrowserVersionString();
            }
            catch { return null; }
        }

        private static async Task CheckWingetAsync(CancellationToken ct)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("winget", "--version")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct,
                    new CancellationTokenSource(3000).Token);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
    }
}
