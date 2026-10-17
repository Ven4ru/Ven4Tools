using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views
{
    public partial class SplashWindow : Window
    {
        /// <summary>
        /// Позы Венчика по этапам предзагрузки: читает каталог, присматривается к правам,
        /// печатает при проверке winget, чешет затылок на предупреждении и радуется готовности.
        /// </summary>
        private enum SplashPose { Reading, Squint, Typing, Scratch, Cheer }

        private sealed record PoseSheet(BitmapSource Image, int Frames);

        // Лента кадров: 8 в ряд, кадр 236×240, 12 кадров в секунду — как у баннера лаунчера.
        private const int FrameColumns = 8, FrameWidth = 236, FrameHeight = 240;
        private const double Fps = 12;

        private readonly CancellationTokenSource _skipCts = new();
        private readonly Dictionary<SplashPose, PoseSheet> _sheets = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _frameTimer;
        private SplashPose _pose;
        private double _poseStart;
        private bool _disposed;

        public SplashWindow()
        {
            InitializeComponent();
            SetPoseNow(SplashPose.Reading);
            _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / Fps) };
            _frameTimer.Tick += (_, _) => ShowFrame();
            Loaded += (_, _) => _frameTimer.Start();
            Closed += (_, _) => _frameTimer.Stop();
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
                SetPose(SplashPose.Reading);
                SetStatus("Загрузка каталога...");
                // Причину сбоя загрузки пишет сам CatalogLoaderService — здесь только
                // не даём ей уронить старт: ниже мы всё равно смотрим на источник каталога.
                try { await CatalogLoaderService.PreloadAsync(ct); } catch (OperationCanceledException) { throw; } catch { }
                ct.ThrowIfCancellationRequested();

                var source = CatalogLoaderService.State.Catalog?.Source;
                if (source == "cache" || source == "embedded")
                {
                    SetPose(SplashPose.Scratch);
                    SetStatus("⚠ Сеть недоступна — каталог из кэша");
                    await Task.Delay(900, ct);
                }
                ct.ThrowIfCancellationRequested();

                // 2. Права администратора
                SetPose(SplashPose.Squint);
                SetStatus("Проверка прав администратора...");
                if (!IsRunningAsAdmin())
                {
                    SetPose(SplashPose.Scratch);
                    SetStatus("⚠ Нет прав администратора — winget может не работать");
                    await Task.Delay(1200, ct);
                }
                ct.ThrowIfCancellationRequested();

                // 3. winget
                SetPose(SplashPose.Typing);
                SetStatus("Проверка winget...");
                bool wingetOk = await Task.Run(async () =>
                {
                    try { return await CheckWingetAsync(ct); } catch { return false; }
                }, ct);
                if (!wingetOk)
                {
                    SetPose(SplashPose.Scratch);
                    SetStatus("⚠ winget не найден — установка приложений может не работать");
                    await Task.Delay(1200, ct);
                }
                ct.ThrowIfCancellationRequested();

                // Готово
                SetPose(SplashPose.Cheer);
                Dispatcher.Invoke(() => btnSkip.Visibility = Visibility.Collapsed);
                SetStatus("Готово!");
                // 1,2 с — чтобы радость Венчика успела проиграться (за 0,7 с она только мелькала).
                await Task.Delay(1200, ct);
            }
            catch (OperationCanceledException) { /* пользователь нажал «Пропустить» */ }
            catch { /* предзагрузка — best-effort: любая ошибка не должна валить старт */ }
            finally { _disposed = true; _skipCts.Dispose(); }
        }

        private void SetStatus(string text) =>
            Dispatcher.Invoke(() => txtStatus.Text = text);

        private void SetPose(SplashPose pose) => Dispatcher.Invoke(() => SetPoseNow(pose));

        private void SetPoseNow(SplashPose pose)
        {
            if (!_sheets.TryGetValue(pose, out var sheet))
            {
                sheet = LoadSheet(pose);
                if (sheet == null) return;
                _sheets[pose] = sheet;
            }
            _pose = pose;
            _poseStart = _clock.Elapsed.TotalSeconds;
            venchikBrush.ImageSource = sheet.Image;
            ShowFrame();
        }

        private static PoseSheet? LoadSheet(SplashPose pose)
        {
            string name = pose.ToString().ToLowerInvariant();
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Resources/Splash/venchik-{name}.png");
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                // Кадров в ленте 60, у «печатает» — 51; последний ряд бывает неполным.
                int frames = pose == SplashPose.Typing ? 51 : 60;
                return new PoseSheet(image, Math.Min(frames, image.PixelHeight / FrameHeight * FrameColumns));
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Splash] Не удалось загрузить кадры Венчика «{name}»: {ex.Message}");
                return null;
            }
        }

        private void ShowFrame()
        {
            if (!_sheets.TryGetValue(_pose, out var sheet)) return;
            // Без анимаций (настройка системы или программы) Венчик стоит на первом кадре.
            int frame = MotionService.Enabled ? (int)((_clock.Elapsed.TotalSeconds - _poseStart) * Fps) % sheet.Frames : 0;
            int col = frame % FrameColumns, row = frame / FrameColumns;
            venchikBrush.Viewbox = new Rect(col * FrameWidth, row * FrameHeight, FrameWidth, FrameHeight);
        }

        private static bool IsRunningAsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
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
