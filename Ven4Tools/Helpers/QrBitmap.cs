using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ven4Tools.Services;

namespace Ven4Tools.Helpers
{
    /// <summary>
    /// Отрисовка QR-кода в картинку WPF. Цвета намеренно не берутся из темы:
    /// камера телефона уверенно читает только тёмные модули на светлом фоне,
    /// поэтому код всегда чёрно-белый, с тихой зоной в 4 модуля по стандарту.
    /// </summary>
    internal static class QrBitmap
    {
        public static BitmapSource Render(string text, int pixelsPerModule = 8, int quietZone = 4)
        {
            bool[,] modules = QrCodeEncoder.Encode(text);
            int size = modules.GetLength(0);
            int side = (size + quietZone * 2) * pixelsPerModule;
            var pixels = new byte[side * side];
            Array.Fill(pixels, (byte)0xFF);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!modules[y, x]) continue;
                    int top = (y + quietZone) * pixelsPerModule;
                    int left = (x + quietZone) * pixelsPerModule;
                    for (int dy = 0; dy < pixelsPerModule; dy++)
                        Array.Fill(pixels, (byte)0x00, (top + dy) * side + left, pixelsPerModule);
                }
            }

            var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
