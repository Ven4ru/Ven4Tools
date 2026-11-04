using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ven4Bar;

/// <summary>
/// Маленькое всплывающее окно с живой миниатюрой окна через DwmRegisterThumbnail.
/// Окно не активируется и не ловит мышь.
/// </summary>
internal sealed class ThumbnailPopup : Window
{
    private const double ThumbW = 240, ThumbH = 150, Pad = 8, TitleH = 22;
    private IntPtr _thumb;
    private IntPtr _source;
    private readonly TextBlock _title;

    public ThumbnailPopup()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = ThumbW + Pad * 2;
        Height = ThumbH + Pad * 2 + TitleH;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x4A));
        BorderThickness = new Thickness(1);

        _title = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(Pad, 4, Pad, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Content = _title;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        };
        Closed += (_, _) => Unregister();
    }

    /// <summary>Показать миниатюру окна над точкой anchor (в DIP-координатах экрана).</summary>
    public void ShowFor(IntPtr source, string title, Point anchorTopCenterDip)
    {
        _title.Text = title;
        Left = Math.Max(0, anchorTopCenterDip.X - Width / 2);
        Top = anchorTopCenterDip.Y - Height - 6;
        if (!IsVisible) Show();

        if (_source != source)
        {
            Unregister();
            _source = source;
            var dest = new WindowInteropHelper(this).Handle;
            if (Native.DwmRegisterThumbnail(dest, source, out _thumb) != 0) _thumb = IntPtr.Zero;
        }
        UpdateThumb();
    }

    private void UpdateThumb()
    {
        if (_thumb == IntPtr.Zero) return;
        Native.DwmQueryThumbnailSourceSize(_thumb, out var size);
        if (size.cx <= 0 || size.cy <= 0) return;

        // Прямоугольник назначения — в физических пикселях клиентской области всплывающего окна.
        double scale = 1.0;
        var ps = PresentationSource.FromVisual(this);
        if (ps?.CompositionTarget != null) scale = ps.CompositionTarget.TransformToDevice.M11;

        double boxW = ThumbW * scale, boxH = ThumbH * scale;
        double k = Math.Min(boxW / size.cx, boxH / size.cy);
        int w = (int)(size.cx * k), h = (int)(size.cy * k);
        int left = (int)(Pad * scale + (boxW - w) / 2);
        int top = (int)((Pad + TitleH) * scale + (boxH - h) / 2);

        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY
                      | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };
        Native.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    public void HidePopup()
    {
        Unregister();
        if (IsVisible) Hide();
    }

    private void Unregister()
    {
        if (_thumb != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
        _source = IntPtr.Zero;
    }
}
