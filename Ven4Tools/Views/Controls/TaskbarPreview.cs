using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Controls
{
    /// <summary>
    /// Витрина «Панель задач» (Персонализация, блок 1): обои пользователя и поверх них панель задач
    /// в выбранной теме и наборе — до того, как что-то применено. Рисуется целиком в OnRender,
    /// без таймеров: перерисовка только при смене свойств и размера.
    /// </summary>
    public sealed class TaskbarPreview : FrameworkElement
    {
        private static FrameworkPropertyMetadata Meta(object def) => new(def, FrameworkPropertyMetadataOptions.AffectsRender);

        public static readonly DependencyProperty WallpaperProperty = DependencyProperty.Register(nameof(Wallpaper), typeof(ImageSource), typeof(TaskbarPreview), Meta(null!));
        public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(nameof(Theme), typeof(TaskbarTheme), typeof(TaskbarPreview), Meta(null!));
        public static readonly DependencyProperty TransparencyProperty = DependencyProperty.Register(nameof(Transparency), typeof(bool), typeof(TaskbarPreview), Meta(true));
        public static readonly DependencyProperty CenteredProperty = DependencyProperty.Register(nameof(Centered), typeof(bool), typeof(TaskbarPreview), Meta(true));
        public static readonly DependencyProperty SearchProperty = DependencyProperty.Register(nameof(Search), typeof(int), typeof(TaskbarPreview), Meta(2));
        public static readonly DependencyProperty TaskViewProperty = DependencyProperty.Register(nameof(TaskView), typeof(bool), typeof(TaskbarPreview), Meta(true));
        public static readonly DependencyProperty WidgetsProperty = DependencyProperty.Register(nameof(Widgets), typeof(bool), typeof(TaskbarPreview), Meta(true));
        public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(bool), typeof(TaskbarPreview), Meta(false));
        public static readonly DependencyProperty EarsProperty = DependencyProperty.Register(nameof(Ears), typeof(bool), typeof(TaskbarPreview), Meta(true));
        public static readonly DependencyProperty VerticalProperty = DependencyProperty.Register(nameof(Vertical), typeof(bool), typeof(TaskbarPreview), Meta(false));
        public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(nameof(Edge), typeof(int), typeof(TaskbarPreview), Meta(3));
        public static readonly DependencyProperty SearchLightProperty = DependencyProperty.Register(nameof(SearchLight), typeof(bool), typeof(TaskbarPreview), Meta(true));
        public static readonly DependencyProperty StartMenuProperty = DependencyProperty.Register(nameof(StartMenu), typeof(bool), typeof(TaskbarPreview), Meta(false));

        public ImageSource? Wallpaper { get => (ImageSource?)GetValue(WallpaperProperty); set => SetValue(WallpaperProperty, value); }
        public TaskbarTheme? Theme { get => (TaskbarTheme?)GetValue(ThemeProperty); set => SetValue(ThemeProperty, value); }
        public bool Transparency { get => (bool)GetValue(TransparencyProperty); set => SetValue(TransparencyProperty, value); }
        public bool Centered { get => (bool)GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }
        /// <summary>0 — скрыт, 1 — значок, 2 — поле, 3 — значок и подпись (как SearchboxTaskbarMode).</summary>
        public int Search { get => (int)GetValue(SearchProperty); set => SetValue(SearchProperty, value); }
        public bool TaskView { get => (bool)GetValue(TaskViewProperty); set => SetValue(TaskViewProperty, value); }
        public bool Widgets { get => (bool)GetValue(WidgetsProperty); set => SetValue(WidgetsProperty, value); }
        public bool Labels { get => (bool)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
        public bool Ears { get => (bool)GetValue(EarsProperty); set => SetValue(EarsProperty, value); }
        /// <summary>Устарело: то же, что Edge = 0.</summary>
        public bool Vertical { get => (bool)GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }
        /// <summary>Край панели: 0 слева, 1 сверху, 2 справа, 3 снизу.</summary>
        public int Edge { get => (int)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
        /// <summary>Поле поиска Windows 11 светлое, пока у приложений светлый режим (AppsUseLightTheme), — как в Windows.</summary>
        public bool SearchLight { get => (bool)GetValue(SearchLightProperty); set => SetValue(SearchLightProperty, value); }
        public bool StartMenu { get => (bool)GetValue(StartMenuProperty); set => SetValue(StartMenuProperty, value); }

        // Без своего «паспорта» элемент не виден экранному диктору и UI Automation.
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
            new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);

        private static readonly Color WallAvg = Color.FromRgb(18, 30, 52);
        private static readonly (Color A, Color B, string Name)[] Apps =
        {
            (Color.FromRgb(0xFF, 0xD3, 0x5C), Color.FromRgb(0xE9, 0xA3, 0x20), "Проводник"),
            (Color.FromRgb(0x7F, 0xD4, 0xFF), Color.FromRgb(0x1D, 0x6F, 0xD6), "Браузер"),
            (Color.FromRgb(0x5F, 0xE0, 0xA0), Color.FromRgb(0x1A, 0x8A, 0x57), "Ven4Tools"),
            (Color.FromRgb(0x9D, 0xB7, 0xFF), Color.FromRgb(0x4A, 0x63, 0xD8), "Почта"),
            (Color.FromRgb(0xFF, 0x9B, 0xB3), Color.FromRgb(0xD6, 0x3E, 0x6A), "Музыка"),
        };

        private static Color C(string hex) { var (r, g, b) = TaskbarThemeService.Rgb(hex); return Color.FromRgb(r, g, b); }
        private static Color Mix(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        private static Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W < 10 || H < 10) return;
            var rect = new Rect(0, 0, W, H);
            dc.PushClip(new RectangleGeometry(rect, 8, 8));

            // обои — «заполнить» по центру
            if (Wallpaper != null && Wallpaper.Width > 0)
            {
                double k = Math.Max(W / Wallpaper.Width, H / Wallpaper.Height);
                double iw = Wallpaper.Width * k, ih = Wallpaper.Height * k;
                dc.DrawImage(Wallpaper, new Rect((W - iw) / 2, (H - ih) / 2, iw, ih));
            }
            else dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(11, 20, 36), Color.FromRgb(20, 52, 68), 30), null, rect);

            var t = Theme ?? TaskbarThemeService.Themes[0];
            var barSolid = t.Prevalence ? C(t.Bar) : t.Dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(238, 238, 238);
            bool glass = Transparency && t.Transparency;
            var barEff = glass ? Mix(WallAvg, barSolid, t.Dark ? .80 : .86) : barSolid;
            var barFill = glass ? Color.FromArgb((byte)(t.Dark ? 204 : 219), barSolid.R, barSolid.G, barSolid.B) : barSolid;
            var fg = t.Dark ? Color.FromRgb(233, 238, 243) : Color.FromRgb(40, 40, 40);
            var dim = t.Dark ? Color.FromRgb(170, 180, 190) : Color.FromRgb(110, 110, 110);
            var hov = t.Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);
            var acc = C(t.Accent);

            double u = Math.Max(0.32, Math.Min(1.1, W / 980.0)); // масштаб: 1 = панель 48 px (и для вертикальной — по ширине витрины)
            double bh = 48 * u;
            int edge = Vertical ? 0 : Edge;
            bool vert = edge is 0 or 2;
            double barX = edge == 2 ? W - bh : 0;
            var bar = vert ? new Rect(barX, 0, bh, H) : new Rect(0, edge == 1 ? 0 : H - bh, W, bh);
            dc.DrawRectangle(Solid(barFill), null, bar);
            dc.DrawRectangle(Solid(Color.FromArgb(18, 255, 255, 255)), null,
                vert ? new Rect(edge == 2 ? barX : bh - 1, 0, 1, H) : new Rect(0, edge == 1 ? bh - 1 : H - bh, W, 1));

            double ic = 36 * u, gap = 4 * u;
            // ── что стоит в ряд (слева направо или сверху вниз) ──
            var items = new System.Collections.Generic.List<(string Kind, int App)>();
            items.Add(("start", -1));
            if (!vert && Search == 2) items.Add(("searchbox", -1));
            else if (Search is 1 or 3 || (vert && Search == 2)) items.Add(("search", -1));
            if (TaskView) items.Add(("taskview", -1));
            for (int i = 0; i < Apps.Length; i++) items.Add(("app", i));

            // Подписи окон («Как в Windows 10») — только если помещаются. На миниатюре набора
            // масштаб упирается в нижний предел, шрифт — в минимальные 6 pt, и подписи
            // вместе с часами наезжали друг на друга: там остаются одни значки.
            // Левая граница области уведомлений — та же арифметика, что в блоке часов ниже.
            // Не хватает места на полные 70 — подпись сужается и обрезается многоточием,
            // меньше 28 — подписей нет.
            double trayLeft = W - (10 + 66 + 6 + 28 + 26 + 16) * u;
            bool labels = Labels && !vert && 11.5 * u >= 6;
            double labelW = 70 * u;
            double Len((string Kind, int App) it) => it.Kind switch
            {
                "searchbox" => 200 * u,
                "app" when labels => ic + labelW,
                _ => ic,
            };
            double total = 0; foreach (var it in items) total += Len(it) + gap;
            total -= gap;
            if (labels)
            {
                double appCount = Apps.Length;
                double room = trayLeft - 8 * u - (Centered ? (W - total) / 2 : 10 * u) - (total - appCount * labelW);
                labelW = Math.Min(70 * u, room / appCount);
                if (labelW < 28 * u) labels = false;
                total = 0; foreach (var it in items) total += Len(it) + gap;
                total -= gap;
            }
            double along = vert ? 10 * u : (Centered ? (W - total) / 2 : 10 * u);
            Point startCenter = default;
            foreach (var it in items)
            {
                double len = Len(it);
                if (vert && along + len > H - 34 * u) break;   // не залезать на часы внизу вертикальной панели
                var cell = vert ? new Rect(barX + (bh - ic) / 2, along, ic, len) : new Rect(along, bar.Top + (bh - ic) / 2, len, ic);
                switch (it.Kind)
                {
                    case "start":
                        startCenter = new Point(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2);
                        DrawStart(dc, cell, u);
                        break;
                    case "searchbox":
                        var pill = new Rect(cell.Left, cell.Top + 2 * u, cell.Width, cell.Height - 4 * u);
                        bool lightBox = !t.Dark || SearchLight;
                        dc.DrawRoundedRectangle(Solid(lightBox ? Color.FromArgb(255, 246, 246, 246) : Color.FromArgb(40, 255, 255, 255)), null, pill, pill.Height / 2, pill.Height / 2);
                        var sdim = lightBox ? Color.FromRgb(96, 96, 96) : dim;
                        DrawGlass(dc, new Point(pill.Left + 18 * u, pill.Top + pill.Height / 2), 6 * u, sdim);
                        Text(dc, "Поиск", new Point(pill.Left + 32 * u, pill.Top + pill.Height / 2), 12 * u, sdim, false);
                        break;
                    case "search":
                        DrawGlass(dc, new Point(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2), 6.5 * u, fg);
                        break;
                    case "taskview":
                        var a = new Rect(cell.Left + cell.Width * .26, cell.Top + cell.Height * .34, cell.Width * .32, cell.Height * .36);
                        var b = new Rect(cell.Left + cell.Width * .44, cell.Top + cell.Height * .26, cell.Width * .32, cell.Height * .34);
                        var pen = new Pen(Solid(fg), 1.6 * u);
                        dc.DrawRoundedRectangle(null, pen, a, 2 * u, 2 * u); dc.DrawRoundedRectangle(null, pen, b, 2 * u, 2 * u);
                        break;
                    case "app":
                        var (ca, cb, name) = Apps[it.App];
                        bool on = it.App is 0 or 2;
                        if (on) dc.DrawRoundedRectangle(Solid(hov), null, cell, 6 * u, 6 * u);
                        var gl = new Rect(cell.Left + (ic - 22 * u) / 2, cell.Top + (ic - 22 * u) / 2, 22 * u, 22 * u);
                        dc.DrawRoundedRectangle(new LinearGradientBrush(ca, cb, 60), null, gl, 5 * u, 5 * u);
                        if (labels) Text(dc, name, new Point(cell.Left + ic, cell.Top + ic / 2), 11.5 * u, fg, false, labelW - 4 * u);
                        var mark = on ? 16 * u : (it.App is 1 or 4 ? 6 * u : 0);
                        if (mark > 0)
                        {
                            var mc = on ? acc : dim;
                            var mr = vert ? new Rect(cell.Left + 1 * u, cell.Top + (ic - mark) / 2, 3 * u, mark)
                                              : new Rect(cell.Left + (labels ? cell.Width : ic) / 2 - mark / 2, cell.Bottom - 3.5 * u, mark, 3 * u);
                            dc.DrawRoundedRectangle(Solid(mc), null, mr, 1.5 * u, 1.5 * u);
                        }
                        break;
                }
                along += len + gap;
            }

            // виджет погоды (слева при значках по центру)
            if (Widgets && !vert && Centered)
            {
                var c0 = new Point(16 * u, bar.Top + bh / 2);
                dc.DrawEllipse(Solid(Color.FromRgb(0xF5, 0xC5, 0x42)), null, new Point(c0.X + 7 * u, c0.Y - 3 * u), 6 * u, 6 * u);
                dc.DrawRoundedRectangle(Solid(Color.FromRgb(0xCF, 0xD8, 0xE3)), null, new Rect(c0.X + 2 * u, c0.Y - 1 * u, 18 * u, 9 * u), 4.5 * u, 4.5 * u);
                // На миниатюре (шрифт упёрся в минимум) — только температура, без «облачно».
                if (10.5 * u >= 6)
                {
                    Text(dc, "+5°", new Point(c0.X + 26 * u, c0.Y - 6 * u), 11.5 * u, fg, false);
                    Text(dc, "облачно", new Point(c0.X + 26 * u, c0.Y + 7 * u), 10.5 * u, dim, false);
                }
                else Text(dc, "+5°", new Point(c0.X + 26 * u, c0.Y), 11.5 * u, fg, false);
            }

            // область уведомлений и часы
            if (!vert)
            {
                double x = W - 10 * u;
                var clockW = 66 * u;
                // Дата — только когда шрифт не упирается в минимальный размер: на миниатюре
                // она шире своего места и залезала на значки трея. Тогда остаются одни часы.
                if (11 * u >= 6)
                {
                    Text(dc, "15:24", new Point(x - clockW / 2, bar.Top + bh / 2 - 7 * u), 12 * u, fg, true);
                    Text(dc, DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), new Point(x - clockW / 2, bar.Top + bh / 2 + 8 * u), 11 * u, fg, true);
                }
                else Text(dc, "15:24", new Point(x - clockW / 2, bar.Top + bh / 2), 12 * u, fg, true);
                x -= clockW + 6 * u;
                var p = new Pen(Solid(fg), 1.5 * u);
                // батарея, громкость, сеть
                dc.DrawRoundedRectangle(null, p, new Rect(x - 18 * u, bar.Top + bh / 2 - 5 * u, 16 * u, 10 * u), 2 * u, 2 * u);
                dc.DrawRectangle(Solid(fg), null, new Rect(x - 16 * u, bar.Top + bh / 2 - 3 * u, 9 * u, 6 * u));
                x -= 28 * u;
                var sp = new StreamGeometry();
                using (var g = sp.Open())
                {
                    double y0 = bar.Top + bh / 2;
                    g.BeginFigure(new Point(x - 14 * u, y0 - 3 * u), true, true);
                    g.LineTo(new Point(x - 11 * u, y0 - 3 * u), true, false); g.LineTo(new Point(x - 7 * u, y0 - 7 * u), true, false);
                    g.LineTo(new Point(x - 7 * u, y0 + 7 * u), true, false); g.LineTo(new Point(x - 11 * u, y0 + 3 * u), true, false);
                    g.LineTo(new Point(x - 14 * u, y0 + 3 * u), true, false);
                }
                sp.Freeze(); dc.DrawGeometry(Solid(fg), null, sp);
                x -= 26 * u;
                // Сеть — веер из трёх дуг над точкой, как значок Wi-Fi в Windows. Прежние полные
                // концентрические круги читались как мишень, а не как сеть.
                var wc = new Point(x - 8 * u, bar.Top + bh / 2 + 5 * u);
                dc.DrawEllipse(Solid(fg), null, wc, 1.5 * u, 1.5 * u);
                var wifiPen = new Pen(Solid(fg), 1.4 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < 3; i++)
                {
                    double r = (4.5 + i * 3.5) * u, d = r * Math.Sqrt(0.5);
                    var arc = new StreamGeometry();
                    using (var g = arc.Open())
                    {
                        g.BeginFigure(new Point(wc.X - d, wc.Y - d), false, false);
                        g.ArcTo(new Point(wc.X + d, wc.Y - d), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    }
                    arc.Freeze();
                    dc.DrawGeometry(null, wifiPen, arc);
                }
            }
            else
            {
                Text(dc, "15:24", new Point(barX + bh / 2, H - 22 * u), 11.5 * u, fg, true);
            }

            // меню «Пуск» (набор «Пуск без лишнего»)
            if (StartMenu && edge == 3)
            {
                double mw = Math.Min(W * .42, 300 * u), mh = Math.Min(H * .62, 260 * u);
                var m = new Rect(Math.Max(6 * u, Math.Min(W - mw - 6 * u, startCenter.X - mw / 2)), bar.Top - mh - 8 * u, mw, mh);
                dc.DrawRoundedRectangle(Solid(glass ? Color.FromArgb(235, barSolid.R, barSolid.G, barSolid.B) : barSolid), new Pen(Solid(Color.FromArgb(30, 255, 255, 255)), 1), m, 8 * u, 8 * u);
                double cs = (mw - 24 * u - 5 * 6 * u) / 6;
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 6; c++)
                    {
                        var (ca, cb, _) = Apps[(r * 6 + c) % Apps.Length];
                        dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromArgb(200, ca.R, ca.G, ca.B), Color.FromArgb(200, cb.R, cb.G, cb.B), 60), null,
                            new Rect(m.Left + 12 * u + c * (cs + 6 * u), m.Top + 14 * u + r * (cs + 10 * u), cs, cs), 4 * u, 4 * u);
                    }
            }

            // ушки над «Пуском» — цвет самой панели, внутри — акцент темы
            if (Ears && edge == 3 && startCenter != default)
            {
                double ew = 50 * u, eh = 14 * u;
                double ex = Math.Max(0, Math.Min(W - ew, startCenter.X - ew / 2)), ey = bar.Top - eh + 1.5 * u;
                DrawEars(dc, new Rect(ex, ey, ew, eh), barEff, C(t.Inner));
            }
            dc.Pop();
        }

        private static void DrawStart(DrawingContext dc, Rect cell, double u)
        {
            double s = 8.4 * u, g = 1.4 * u, w = s * 2 + g;
            double x = cell.Left + (cell.Width - w) / 2, y = cell.Top + (cell.Height - w) / 2;
            var br = new LinearGradientBrush(Color.FromRgb(0x63, 0xD3, 0xFF), Color.FromRgb(0x10, 0x84, 0xE8), 70);
            br.Freeze();
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
                dc.DrawRoundedRectangle(br, null, new Rect(x + i * (s + g), y + j * (s + g), s, s), 1.4 * u, 1.4 * u);
        }

        private static void DrawGlass(DrawingContext dc, Point c, double r, Color col)
        {
            var p = new Pen(Solid(col), Math.Max(1, r / 3.2));
            dc.DrawEllipse(null, p, new Point(c.X - r * .25, c.Y - r * .25), r * .75, r * .75);
            dc.DrawLine(p, new Point(c.X + r * .3, c.Y + r * .3), new Point(c.X + r * .95, c.Y + r * .95));
        }

        private void Text(DrawingContext dc, string s, Point at, double size, Color col, bool center, double maxWidth = 0)
        {
            // Текст рисуется мимо TextBlock — переводчик интерфейса его не видит, переводим здесь.
            var ft = new FormattedText(Tr(s), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                Math.Max(6, size), Solid(col), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (maxWidth > 0)
            {
                ft.MaxTextWidth = maxWidth;
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
            dc.DrawText(ft, new Point(center ? at.X - ft.Width / 2 : at.X, at.Y - ft.Height / 2));
        }

        /// <summary>Ушки в координатах 62×15 (те же кривые, что у программы ушек).</summary>
        public static void DrawEars(DrawingContext dc, Rect r, Color outer, Color inner)
        {
            dc.PushTransform(new MatrixTransform(r.Width / 62, 0, 0, r.Height / 15, r.Left, r.Top));
            var o = Solid(outer); var i = Solid(Color.FromArgb(230, inner.R, inner.G, inner.B));
            dc.DrawGeometry(o, null, Ear(1, 15.5, 12.5, 22.5)); dc.DrawGeometry(o, null, Ear(39.5, 15.5, 49.5, 61));
            dc.DrawGeometry(i, null, Ear(6.2, 15.5, 13.6, 20.2)); dc.DrawGeometry(i, null, Ear(41.8, 15.5, 48.4, 55.8));
            dc.Pop();
        }

        private static Geometry Ear(double x0, double y, double tipX, double x1)
        {
            var top = Math.Max(y - (x1 - x0) * .66, .8);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, y), true, true);
                c.BezierTo(new Point(x0 + (tipX - x0) * .45, y - (y - top) * .55), new Point(tipX - 1.6, top), new Point(tipX, top), true, false);
                c.BezierTo(new Point(tipX + 1.6, top), new Point(x1 - (x1 - tipX) * .35, top + (y - top) * .25), new Point(x1, y), true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
