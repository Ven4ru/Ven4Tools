using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Runtime.InteropServices;

namespace Ven4Tools.UITests;

/// <summary>
/// Снимок содержимого окна через PrintWindow.
/// <para>
/// Захват области экрана (FlaUI Capture.Element) снимает то, что физически
/// оказалось сверху: если окно не удалось поднять, в кадр попадает чужое окно,
/// и снапшот-тест краснеет при исправном лаунчере. PrintWindow рисует окно в
/// собственный контекст устройства независимо от z-порядка и перекрытий.
/// </para>
/// <para>
/// System.Drawing.Common сознательно НЕ используется: её убрали из проекта
/// из-за уязвимости в транзитивной зависимости. Пиксели читаются из DIB-секции
/// напрямую и отдаются в ImageSharp.
/// </para>
/// </summary>
internal static class WindowCapture
{
    /// <summary>Рисовать всё содержимое, включая слои, отрисованные аппаратно.</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    public static Image<Rgba32> Capture(IntPtr windowHandle)
    {
        if (!GetWindowRect(windowHandle, out RECT rect))
            throw new InvalidOperationException("Не удалось получить границы окна.");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Некорректный размер окна: {width}x{height}.");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                // Отрицательная высота — строки сверху вниз, как ожидает ImageSharp.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            };

            bitmap = CreateDIBSection(memoryDc, ref header, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось создать буфер для снимка окна.");

            previous = SelectObject(memoryDc, bitmap);

            if (!PrintWindow(windowHandle, memoryDc, PW_RENDERFULLCONTENT))
                throw new InvalidOperationException("PrintWindow не смог отрисовать окно.");

            int byteCount = width * height * 4;
            byte[] buffer = new byte[byteCount];
            Marshal.Copy(bits, buffer, 0, byteCount);

            // GDI отдаёт BGRA, ImageSharp здесь ждёт RGBA — меняем местами каналы.
            for (int i = 0; i < byteCount; i += 4)
            {
                (buffer[i], buffer[i + 2]) = (buffer[i + 2], buffer[i]);
                buffer[i + 3] = 255; // альфа от GDI недостоверна, кадр непрозрачный
            }

            return Image.LoadPixelData<Rgba32>(buffer, width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
