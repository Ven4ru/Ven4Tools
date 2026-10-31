using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ven4Tools.Screensaver
{
    /// <summary>
    /// Ночной компьютерный класс: на тёмном небе мерцают звёзды, вверх уплывают пакеты
    /// с программами, а по полу бегает Венчик. Цвета — как на ven4tools.ru.
    /// </summary>
    internal sealed class SaverForm : Form
    {
        private const int RunFrames = 14;
        private static readonly Color Accent = Color.FromArgb(74, 222, 128);
        private static readonly Color SkyTop = Color.FromArgb(10, 13, 21);
        private static readonly Color SkyBottom = Color.FromArgb(18, 26, 42);

        private readonly bool _preview;
        private readonly Timer _timer = new Timer { Interval = 33 };
        private readonly Random _rnd = new Random();
        private readonly Bitmap _run;
        private readonly List<PointF> _stars = new List<PointF>();
        private readonly List<float> _starPhase = new List<float>();
        private readonly List<Package> _packages = new List<Package>();
        private Point? _mouseStart;
        private float _catX;
        private int _tick;

        private sealed class Package { public float X, Y, Speed, Size, Wobble; }

        /// <summary>Заставка на весь экран.</summary>
        public SaverForm(Rectangle bounds) : this(false)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            Cursor.Hide();
        }

        /// <summary>Предпросмотр внутри окна параметров Windows.</summary>
        public SaverForm(IntPtr parent) : this(true)
        {
            Native.SetParent(Handle, parent);
            Native.SetWindowLong(Handle, Native.GWL_STYLE, Native.GetWindowLong(Handle, Native.GWL_STYLE) | Native.WS_CHILD);
            Bounds = Native.ClientRect(parent);
        }

        private SaverForm(bool preview)
        {
            _preview = preview;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = SkyTop;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            using (var stream = typeof(SaverForm).Assembly.GetManifestResourceStream("run.png"))
                _run = new Bitmap(stream!);

            _timer.Tick += (s, e) => Step();
            Load += (s, e) => { Seed(); _timer.Start(); };
            Resize += (s, e) => Seed();
        }

        private void Seed()
        {
            _stars.Clear();
            _starPhase.Clear();
            var count = Math.Max(20, Width * Height / 9000);
            for (var i = 0; i < count; i++)
            {
                _stars.Add(new PointF((float)_rnd.NextDouble() * Width, (float)_rnd.NextDouble() * Height * 0.7f));
                _starPhase.Add((float)(_rnd.NextDouble() * Math.PI * 2));
            }
            _packages.Clear();
            _catX = -CatWidth();
        }

        private float CatHeight() => Math.Max(24, Height * 0.2f);
        private float CatWidth() => CatHeight() * _run.Width / RunFrames / _run.Height;

        private void Step()
        {
            _tick++;
            _catX += Math.Max(1.5f, Width / 420f);
            if (_catX > Width) _catX = -CatWidth();

            if (_tick % 25 == 0 && _packages.Count < 12)
            {
                var size = Math.Max(8, Height * (0.03f + (float)_rnd.NextDouble() * 0.03f));
                _packages.Add(new Package
                {
                    X = (float)_rnd.NextDouble() * Width,
                    Y = Height + size,
                    Size = size,
                    Speed = Math.Max(0.5f, Height / 700f) * (0.6f + (float)_rnd.NextDouble()),
                    Wobble = (float)(_rnd.NextDouble() * Math.PI * 2),
                });
            }
            foreach (var p in _packages) p.Y -= p.Speed;
            _packages.RemoveAll(p => p.Y < -p.Size * 2);

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;

            using (var sky = new LinearGradientBrush(ClientRectangle, SkyTop, SkyBottom, LinearGradientMode.Vertical))
                g.FillRectangle(sky, ClientRectangle);

            for (var i = 0; i < _stars.Count; i++)
            {
                var a = (int)(110 + 110 * Math.Sin(_tick * 0.05 + _starPhase[i]));
                var r = _preview ? 1f : Math.Max(1.2f, Height / 600f);
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, a)), 220, 230, 255)))
                    g.FillEllipse(b, _stars[i].X, _stars[i].Y, r, r);
            }

            foreach (var p in _packages)
            {
                var x = p.X + (float)Math.Sin(_tick * 0.03 + p.Wobble) * p.Size * 0.6f;
                using (var path = Rounded(new RectangleF(x, p.Y, p.Size, p.Size), p.Size * 0.25f))
                using (var fill = new SolidBrush(Color.FromArgb(70, Accent)))
                using (var pen = new Pen(Color.FromArgb(180, Accent), Math.Max(1, p.Size / 14)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
            }

            // Пол: тонкая зелёная линия, как дорожка в игре.
            var floor = Height * 0.86f;
            using (var pen = new Pen(Color.FromArgb(90, Accent), Math.Max(1, Height / 400f)))
                g.DrawLine(pen, 0, floor, Width, floor);

            var frame = (_tick / 2) % RunFrames;
            var fw = _run.Width / RunFrames;
            var dest = new RectangleF(_catX, floor - CatHeight(), CatWidth(), CatHeight());
            g.DrawImage(_run, dest, new RectangleF(frame * fw, 0, fw, _run.Height), GraphicsUnit.Pixel);

            if (!_preview)
            {
                using (var font = new Font("Segoe UI", Math.Max(9, Height / 70f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                {
                    var text = "Ven4Tools";
                    var size = g.MeasureString(text, font);
                    g.DrawString(text, font, b, Width - size.Width - Height / 40f, Height - size.Height - Height / 40f);
                }
            }
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Заставка закрывается от любого действия пользователя; предпросмотр — только вместе с окном параметров.
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_preview) return;
            if (_mouseStart == null) { _mouseStart = e.Location; return; }
            if (Math.Abs(e.X - _mouseStart.Value.X) > 8 || Math.Abs(e.Y - _mouseStart.Value.Y) > 8) Application.Exit();
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (!_preview) Application.Exit(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (!_preview) Application.Exit(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _run.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
