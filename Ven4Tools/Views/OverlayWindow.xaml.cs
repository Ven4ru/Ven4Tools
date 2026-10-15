using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Оверлей поверх игр: маленькое окно в углу экрана, прозрачное для мыши, без фокуса
    /// и без кнопки на панели задач. Это обычное окно поверх остальных — в процесс игры
    /// ничего не внедряется. Видно в оконном режиме и «полноэкранном в окне»; в
    /// исключительном полноэкранном режиме игра рисует мимо рабочего стола и окна не видно.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        public OverlayWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
            };
        }

        private static readonly Dictionary<Services.ThermalMood, System.Windows.Media.Imaging.BitmapImage> Mascots = new()
        {
            [Services.ThermalMood.Hot] = Load("hot"),
            [Services.ThermalMood.Ok] = Load("ok"),
            [Services.ThermalMood.Cold] = Load("cold"),
        };

        private static System.Windows.Media.Imaging.BitmapImage Load(string name)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Ven4Tools;component/Resources/Overlay/venchik-{name}.png"));
            image.Freeze();
            return image;
        }

        /// <summary>Венчик рядом с цифрами; null — не показывать. В расширенном виде крупнее.</summary>
        public void SetMascot(Services.ThermalMood? mood)
        {
            if (mood == null) { imgMascot.Visibility = Visibility.Collapsed; return; }
            imgMascot.Source = Mascots[mood.Value];
            // Все три позы лежат на одинаковом полотне в одном масштабе Венчика — задаём
            // высоту, а не ширину: иначе поза с каплями или снежинками по бокам вышла бы мельче.
            imgMascot.Height = pnlExtended.Visibility == Visibility.Visible ? 120 : 84;
            imgMascot.Visibility = Visibility.Visible;
        }

        public void SetLines(IReadOnlyList<string> lines)
        {
            pnlExtended.Visibility = Visibility.Collapsed;
            lstLines.Visibility = Visibility.Visible;
            lstLines.ItemsSource = lines;
        }

        /// <summary>Расширенный вид: строки разделов и два графика.</summary>
        public void SetExtended(IReadOnlyList<Services.OverlayLine> lines, IReadOnlyList<double> frameMs,
                                IReadOnlyList<double> cpuLoad, IReadOnlyList<double> gpuLoad)
        {
            lstLines.Visibility = Visibility.Collapsed;
            pnlExtended.Visibility = Visibility.Visible;
            lstExtended.ItemsSource = lines;
            pnlExtended.UpdateLayout();

            // Время кадра: шкала от 0 до самого долгого кадра, но не меньше 33 мс (30 FPS),
            // чтобы ровная игра выглядела ровной линией внизу, а не «пилой» во весь график.
            double top = Math.Max(33.3, frameMs.Count > 0 ? frameMs.Max() : 0);
            plFrames.Points = Plot(frameMs, cnvFrames, top);
            txtFrameMax.Text = frameMs.Count > 0 ? $"{top:0} {Tr("мс")}" : "";
            plCpuLoad.Points = Plot(cpuLoad, cnvLoad, 100);
            plGpuLoad.Points = Plot(gpuLoad, cnvLoad, 100);
        }

        private static PointCollection Plot(IReadOnlyList<double> values, FrameworkElement area, double top)
        {
            var points = new PointCollection();
            double w = area.ActualWidth > 0 ? area.ActualWidth : 400, h = area.ActualHeight > 0 ? area.ActualHeight : 54;
            if (values.Count < 2) return points;
            for (int i = 0; i < values.Count; i++)
                points.Add(new Point(w * i / (values.Count - 1), h - 2 - (h - 4) * Math.Clamp(values[i] / top, 0, 1)));
            return points;
        }

        /// <summary>Игра, развернувшаяся на весь экран, могла встать выше — поднять окно снова, не забирая фокус.</summary>
        public void KeepOnTop()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
