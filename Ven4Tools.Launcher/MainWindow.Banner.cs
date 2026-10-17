using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ven4Tools.Shared;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Баннер на карточке клиента: Венчик, подпись, проценты и полоса.
    ///
    /// Полоса показывает настоящий ход операции: этапы приходят из <see cref="SetOperationStage"/>,
    /// проценты — из полосы <c>progressDownload</c>. В простое полоса медленно «дышит». Венчик
    /// меняет позу по состоянию: отдыхает в простое, печатает за работой, радуется успеху и
    /// устало трёт глаза, если операция прервалась. Кадры — ленты по 8 в ряд, 12 кадров в секунду.
    /// </summary>
    public partial class MainWindow
    {
        private enum BannerPose { Relax, Typing, Joy, Tired }

        private sealed record BannerSheet(BitmapSource Image, int Frames, int Columns, int Width, int Height);

        private const double BannerFps = 12;
        // Сколько держится радость или усталость после конца операции, прежде чем Венчик снова отдыхает.
        private static readonly TimeSpan BannerAftermath = TimeSpan.FromSeconds(6);
        // Цикл «дыхания» полосы в простое: заполнение, пауза, угасание.
        private const double IdleFill = 4.0, IdleHold = 1.0, IdleFade = 0.8;

        private readonly Dictionary<BannerPose, BannerSheet> _bannerSheets = new();
        private readonly Stopwatch _bannerClock = Stopwatch.StartNew();
        private DispatcherTimer? _bannerTimer;
        private BannerPose _bannerPose = BannerPose.Relax;
        private double _bannerPoseStart;
        private bool _bannerBusy;
        private bool _bannerStaged;
        private double _bannerTarget;
        private double _bannerShown;
        private double _bannerIdleStart;
        private DateTime _bannerAftermathUntil = DateTime.MinValue;

        private void InitBanner()
        {
            SetBannerPose(BannerPose.Relax);
            progressDownload.ValueChanged += (_, e) => OnBannerProgress(e.NewValue);

            _bannerTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / BannerFps) };
            _bannerTimer.Tick += (_, _) => BannerTick();
            // Свёрнутое или спрятанное в трей окно не рисует — и таймер ему не нужен.
            IsVisibleChanged += (_, _) => UpdateBannerTimer();
            StateChanged += (_, _) => UpdateBannerTimer();
            UpdateBannerTimer();
        }

        private void UpdateBannerTimer()
        {
            if (_bannerTimer == null) return;
            bool show = IsVisible && WindowState != WindowState.Minimized;
            if (show && !_bannerTimer.IsEnabled) _bannerTimer.Start();
            else if (!show && _bannerTimer.IsEnabled) _bannerTimer.Stop();
        }

        /// <summary>Этап операции: 1–4 — идёт работа, 5 — готово, 0 — сброс (отмена или ошибка).</summary>
        private void OnBannerStage(int stage)
        {
            if (stage is >= 1 and <= 4)
            {
                _bannerBusy = true;
                _bannerStaged = true;
                bannerStatus.Text = stage switch
                {
                    1 => "Идёт загрузка",
                    2 => "Идёт проверка",
                    3 => "Идёт распаковка",
                    _ => "Идёт установка"
                };
                SetBannerPose(BannerPose.Typing);
            }
            else if (stage == 5)
            {
                FinishBanner(success: true, "Готово");
            }
            else if (_bannerBusy)
            {
                // Строка статуса уже объясняет, что случилось («Отменено», «Ошибка»…).
                FinishBanner(success: false, string.IsNullOrWhiteSpace(txtDownloadStatus.Text) ? "Остановлено" : txtDownloadStatus.Text);
            }
        }

        private void OnBannerProgress(double value)
        {
            _bannerTarget = Math.Clamp(value, 0, 100);
            // Установка компонентов идёт без этапов — только с процентами.
            if (_bannerStaged) return;
            if (!_bannerBusy && value > 0 && value < 100)
            {
                _bannerBusy = true;
                bannerStatus.Text = "Установка компонентов";
                SetBannerPose(BannerPose.Typing);
            }
            else if (_bannerBusy && value >= 100) FinishBanner(success: true, "Готово");
            else if (_bannerBusy && value <= 0) FinishBanner(success: false, "Остановлено");
        }

        private void FinishBanner(bool success, string status)
        {
            _bannerBusy = false;
            _bannerStaged = false;
            if (success) { _bannerTarget = 100; _bannerShown = 100; }
            bannerStatus.Text = status;
            SetBannerPose(success ? BannerPose.Joy : BannerPose.Tired);
            _bannerAftermathUntil = DateTime.UtcNow + BannerAftermath;
        }

        private void SetBannerPose(BannerPose pose)
        {
            if (!_bannerSheets.TryGetValue(pose, out var sheet))
            {
                sheet = LoadBannerSheet(pose);
                if (sheet == null) return;
                _bannerSheets[pose] = sheet;
            }
            _bannerPose = pose;
            _bannerPoseStart = _bannerClock.Elapsed.TotalSeconds;
            bannerVenchikBrush.ImageSource = sheet.Image;
            ShowBannerFrame(sheet, 0);
        }

        private BannerSheet? LoadBannerSheet(BannerPose pose)
        {
            string name = pose.ToString().ToLowerInvariant();
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri($"pack://application:,,,/Resources/Banner/venchik-{name}.png");
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                // Размер кадра постоянный, число кадров — по высоте ленты (последний ряд может быть неполным).
                const int columns = 8, width = 236, height = 240;
                int rows = image.PixelHeight / height;
                int frames = pose == BannerPose.Typing ? 51 : 60;
                frames = Math.Min(frames, rows * columns);
                return new BannerSheet(image, frames, columns, width, height);
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Не удалось загрузить кадры баннера «{name}»: {ex.Message}");
                return null;
            }
        }

        private void ShowBannerFrame(BannerSheet sheet, int frame)
        {
            int col = frame % sheet.Columns, row = frame / sheet.Columns;
            bannerVenchikBrush.Viewbox = new Rect(col * sheet.Width, row * sheet.Height, sheet.Width, sheet.Height);
        }

        private void BannerTick()
        {
            double now = _bannerClock.Elapsed.TotalSeconds;

            if (!_bannerBusy && (_bannerPose is BannerPose.Joy or BannerPose.Tired) && DateTime.UtcNow >= _bannerAftermathUntil)
            {
                SetBannerPose(BannerPose.Relax);
                bannerStatus.Text = "Готов к работе";
                _bannerIdleStart = now;
                _bannerShown = 0;
            }

            if (_bannerSheets.TryGetValue(_bannerPose, out var sheet))
            {
                // Без анимаций (настройка системы/программы) Венчик стоит на первом кадре.
                int frame = MotionService.Enabled ? (int)((now - _bannerPoseStart) * BannerFps) % sheet.Frames : 0;
                ShowBannerFrame(sheet, frame);
            }

            double trackWidth = bannerTrack.ActualWidth;
            if (trackWidth <= 0) return;

            double fraction;
            bool idle = !_bannerBusy && _bannerPose == BannerPose.Relax;
            if (idle)
            {
                // Спокойная петля: полоса заполняется, держится и гаснет.
                double cycle = IdleFill + IdleHold + IdleFade;
                double t = MotionService.Enabled ? (now - _bannerIdleStart) % cycle : IdleFill;
                fraction = t < IdleFill ? EaseInOut(t / IdleFill) : 1;
                bannerFill.Opacity = t < IdleFill + IdleHold ? 0.55 : 0.55 * (1 - (t - IdleFill - IdleHold) / IdleFade);
                bannerPercent.Text = "";
            }
            else
            {
                // Настоящий ход операции: полоса догоняет проценты плавно, а не рывками.
                _bannerShown += (_bannerTarget - _bannerShown) * (MotionService.Enabled ? 0.25 : 1);
                fraction = _bannerShown / 100;
                bannerFill.Opacity = 1;
                bannerPercent.Text = $"{Math.Round(_bannerTarget)}%";
            }
            bannerFill.Width = Math.Max(0, fraction * trackWidth);

            // Блик пробегает по заливке раз в 2,4 с.
            if (MotionService.Enabled)
            {
                double p = now % 2.4 / 1.6;
                bannerShine.X = p <= 1 ? -160 + p * (bannerFill.Width + 160) : -160;
            }
        }

        private static double EaseInOut(double t) => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }
}
