using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ven4Tools.UITests;

/// <summary>
/// Кадр окна в памяти: пиксели BGRA по четыре байта, строки сверху вниз.
/// <para>
/// PNG читается и пишется встроенным в Windows кодеком (WIC, классы
/// System.Windows.Media.Imaging) — отдельная библиотека для этого не нужна. Проверкам
/// от картинки требуется немногое: сохранить, прочитать, обрезать рамку и сравнить
/// пиксели.
/// </para>
/// </summary>
internal sealed class PixelFrame
{
    private const int BytesPerPixel = 4;

    public PixelFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Некорректный размер кадра: {width}x{height}.");
        if (pixels.Length != width * height * BytesPerPixel)
            throw new ArgumentException("Размер буфера не соответствует размеру кадра.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Пиксели в порядке B, G, R, A.</summary>
    public byte[] Pixels { get; }

    public PixelFrame Crop(int left, int top, int width, int height)
    {
        if (left < 0 || top < 0 || width <= 0 || height <= 0 || left + width > Width || top + height > Height)
            throw new ArgumentException($"Область {width}x{height} от ({left}, {top}) не помещается в кадр {Width}x{Height}.");

        byte[] cropped = new byte[width * height * BytesPerPixel];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                Pixels, (((top + row) * Width) + left) * BytesPerPixel,
                cropped, row * width * BytesPerPixel,
                width * BytesPerPixel);
        }
        return new PixelFrame(width, height, cropped);
    }

    public void SavePng(string path)
    {
        BitmapSource source = BitmapSource.Create(
            Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Width * BytesPerPixel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    public static PixelFrame LoadPng(string path)
    {
        using FileStream stream = File.OpenRead(path);
        // Цветовой профиль не применяется: сравниваются сами значения пикселей из файла.
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        byte[] pixels = new byte[frame.PixelWidth * frame.PixelHeight * BytesPerPixel];
        frame.CopyPixels(Int32Rect.Empty, pixels, frame.PixelWidth * BytesPerPixel, 0);
        return new PixelFrame(frame.PixelWidth, frame.PixelHeight, pixels);
    }
}
