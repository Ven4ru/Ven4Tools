using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ven4Start;

/// <summary>
/// Значки программ: SHCreateItemFromParsingName("shell:AppsFolder\…") → IShellItemImageFactory::GetImage
/// (документированный интерфейс оболочки). Читаются в отдельном STA-потоке, в памяти — кэш; готовый
/// значок отдаётся в поток окна через колбэк. Первый показ меню — без значков, они проявляются по мере загрузки.
/// </summary>
internal static class Icons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);

    private static readonly Guid IID_Factory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingQueue Queue = new();
    private static Thread? _worker;

    public static ImageSource? TryGet(string parsing) => Cache.TryGetValue(parsing, out var i) ? i : null;

    /// <summary>Запросить значок; когда будет готов — <paramref name="ready"/> в потоке окна.</summary>
    public static void Request(string parsing, int px, Action<ImageSource> ready, System.Windows.Threading.Dispatcher ui)
    {
        if (Cache.TryGetValue(parsing, out var have)) { if (have != null) ready(have); return; }
        Queue.Add((parsing, px, ready, ui));
        if (_worker == null)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "Ven4Start.Icons" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static void Work()
    {
        while (true)
        {
            var (parsing, px, ready, ui) = Queue.Take();
            if (!Cache.TryGetValue(parsing, out var img))
            {
                img = Load(parsing, px);
                Cache[parsing] = img;
            }
            if (img != null) ui.BeginInvoke(ready, img);
        }
    }

    private static ImageSource? Load(string parsing, int px)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = IID_Factory;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + parsing, IntPtr.Zero, ref iid, out var f) != 0 || f == null) return null;
            try
            {
                // SIIGBF_ICONONLY (0x4) | SIIGBF_BIGGERSIZEOK (0x1): только значок, без эскиза содержимого
                if (f.GetImage(new SIZE { cx = px, cy = px }, 0x5, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                // HBITMAP оболочки — 32-битный DIB с предумноженной альфой, строки снизу вверх.
                // CreateBitmapSourceFromHBitmap альфу теряет (чёрный фон), поэтому копируем биты сами.
                if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32) return null;
                int stride = bm.bmWidthBytes, h = Math.Abs(bm.bmHeight);
                var raw = new byte[stride * h];
                Marshal.Copy(bm.bmBits, raw, 0, raw.Length);
                var flipped = new byte[raw.Length];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, y * stride, flipped, (h - 1 - y) * stride, stride);
                var bmp = BitmapSource.Create(bm.bmWidth, h, 96, 96, PixelFormats.Pbgra32, null, bm.bmHeight > 0 ? flipped : raw, stride);
                bmp.Freeze();
                return bmp;
            }
            finally { Marshal.ReleaseComObject(f); }
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    private sealed class BlockingQueue
    {
        private readonly Queue<(string, int, Action<ImageSource>, System.Windows.Threading.Dispatcher)> _q = new();
        public void Add((string, int, Action<ImageSource>, System.Windows.Threading.Dispatcher) x) { lock (_q) { _q.Enqueue(x); Monitor.Pulse(_q); } }
        public (string, int, Action<ImageSource>, System.Windows.Threading.Dispatcher) Take()
        {
            lock (_q) { while (_q.Count == 0) Monitor.Wait(_q); return _q.Dequeue(); }
        }
    }
}
