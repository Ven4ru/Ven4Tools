using System;
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
        private bool _disposed;

        public SplashWindow()
        {
            InitializeComponent();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            btnSkip.IsEnabled = false;
            // Кнопку могли нажать после того, как RunPreloadAsync уже завершился
            // и освободил CTS — иначе Cancel бросит ObjectDisposedException.
            if (_disposed || _skipCts.IsCancellationRequested) return;
            _skipCts.Cancel();
        }

        public async Task RunPreloadAsync()
        {
            var ct = _skipCts.Token;
            try
            {
                // 1. Каталог — по результату определяем доступность сети
                SetStatus("Загрузка каталога...");
                try { await CatalogLoaderService.PreloadAsync(ct); } catch (OperationCanceledException) { throw; } catch { }
                ct.ThrowIfCancellationRequested();

                var source = CatalogLoaderService.LoadedCatalog?.Source;
                if (source == "cache" || source == "embedded")
                {
                    SetStatus("⚠ Сеть недоступна — каталог из кэша");
                    await Task.Delay(900, ct);
                }
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
                bool wingetOk = await Task.Run(async () =>
                {
                    try { return await CheckWingetAsync(ct); } catch { return false; }
                }, ct);
                if (!wingetOk)
                {
                    SetStatus("⚠ winget не найден — установка приложений может не работать");
                    await Task.Delay(1200, ct);
                }
                ct.ThrowIfCancellationRequested();

                // Готово
                SetImage(_readyUri);
                Dispatcher.Invoke(() => btnSkip.Visibility = Visibility.Collapsed);
                SetStatus("Готово!");
                await Task.Delay(700, ct);
            }
            catch (OperationCanceledException) { /* пользователь нажал «Пропустить» */ }
            catch { /* предзагрузка — best-effort: любая ошибка не должна валить старт */ }
            finally { _disposed = true; _skipCts.Dispose(); }
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

        /// <summary>
        /// Проверяет наличие winget. Возвращает true, если winget доступен
        /// (вышел с кодом 0 и вернул строку версии).
        /// </summary>
        private static async Task<bool> CheckWingetAsync(CancellationToken ct)
        {
            var wingetPath = Ven4Tools.Services.TrustedExecutablePaths.ResolveWinget();
            if (wingetPath == null) return false;

            var psi = new System.Diagnostics.ProcessStartInfo(wingetPath, "--version")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            System.Diagnostics.Process? p;
            try
            {
                p = System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // winget не установлен
            }
            catch (System.IO.FileNotFoundException)
            {
                return false;
            }

            if (p == null) return false;

            using (p)
            {
                using var timeoutCts = new CancellationTokenSource(3000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var outputTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = Task.Run(() => p.StandardError.ReadToEnd()); // дренаж stderr
                try
                {
                    await p.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                    string output = await outputTask.ConfigureAwait(false);
                    await stderrTask.ConfigureAwait(false);
                    return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(); } catch { }
                    // Таймаут проверки трактуем как «недоступен», но без блокировки старта
                    return false;
                }
            }
        }
    }
}
