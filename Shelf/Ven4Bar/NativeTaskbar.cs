using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ven4Bar;

/// <summary>
/// Автоскрытие родной панели задач через SHAppBarMessage(ABM_GETSTATE/ABM_SETSTATE) —
/// то же самое, что галочка «Автоматически скрывать панель задач» в параметрах.
/// Прежнее состояние запоминается в памяти и в файле (на случай аварийного завершения),
/// и возвращается при выходе. Чужие окна не трогаем (никаких ShowWindow).
/// </summary>
internal static class NativeTaskbar
{
    private static readonly object Lock = new();
    private static IntPtr? _savedState;
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ven4Bar", "native-taskbar-state.txt");

    public static bool IsAutoHideForcedByUs => _savedState.HasValue;

    private static Native.APPBARDATA Data()
    {
        return new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            // ABM_GETSTATE/ABM_SETSTATE окно не нужно (так же делает блок 1 клиента, проверено 02.11.2026) —
            // недокументированный класс Shell_TrayWnd не используем.
            hWnd = IntPtr.Zero,
        };
    }

    public static int GetState()
    {
        var abd = Data();
        return (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToUInt32();
    }

    private static void SetState(int state)
    {
        var abd = Data();
        abd.lParam = new IntPtr(state);
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
    }

    /// <summary>
    /// Если прошлый запуск упал и не вернул состояние — вернуть его сейчас.
    /// </summary>
    public static void RestoreAfterCrash()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            if (int.TryParse(File.ReadAllText(StateFile).Trim(), out int st))
                SetState(st);
            File.Delete(StateFile);
        }
        catch
        {
            // не критично
        }
    }

    public static void EnableAutoHide()
    {
        lock (Lock)
        {
            if (_savedState.HasValue) return;
            int prev = GetState();
            _savedState = new IntPtr(prev);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                File.WriteAllText(StateFile, prev.ToString());
            }
            catch
            {
                // без файла тоже работаем, просто без защиты от аварии
            }
            SetState(prev | Native.ABS_AUTOHIDE);
        }
    }

    /// <summary>Временно показать родную панель (снять автоскрытие), не забывая прежнее состояние.</summary>
    public static void TemporarilyShow()
    {
        lock (Lock)
        {
            if (!_savedState.HasValue) return;
            SetState(_savedState.Value.ToInt32() & ~Native.ABS_AUTOHIDE);
        }
    }

    /// <summary>Снова включить автоскрытие после TemporarilyShow.</summary>
    public static void HideAgain()
    {
        lock (Lock)
        {
            if (!_savedState.HasValue) return;
            SetState(_savedState.Value.ToInt32() | Native.ABS_AUTOHIDE);
        }
    }

    public static bool IsAutoHideNow() => (GetState() & Native.ABS_AUTOHIDE) != 0;

    /// <summary>Вернуть как было. Безопасно вызывать много раз.</summary>
    public static void Restore()
    {
        lock (Lock)
        {
            if (!_savedState.HasValue) return;
            SetState(_savedState.Value.ToInt32());
            _savedState = null;
            try { File.Delete(StateFile); } catch { }
        }
    }
}
