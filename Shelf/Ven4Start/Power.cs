using System;
using System.Runtime.InteropServices;

namespace Ven4Start;

internal enum PowerAction { Lock, Sleep, Restart, Shutdown }

internal static class Power
{
    public static string Title(PowerAction a) => a switch
    {
        PowerAction.Lock => "Заблокировать",
        PowerAction.Sleep => "Спящий режим",
        PowerAction.Restart => "Перезагрузить",
        PowerAction.Shutdown => "Выключить",
        _ => a.ToString(),
    };

    /// <summary>Нужно ли спрашивать подтверждение (блокировку не спрашиваем).</summary>
    public static bool NeedsConfirm(PowerAction a) => a != PowerAction.Lock;

    public static bool Run(PowerAction a)
    {
        switch (a)
        {
            case PowerAction.Lock:
                return Native.LockWorkStation();
            case PowerAction.Sleep:
                return Native.SetSuspendState(false, false, false);
            case PowerAction.Restart:
                return EnableShutdownPrivilege() &&
                       Native.ExitWindowsEx(Native.EWX_REBOOT, Native.SHTDN_REASON_MAJOR_OTHER | Native.SHTDN_REASON_FLAG_PLANNED);
            case PowerAction.Shutdown:
                return EnableShutdownPrivilege() &&
                       Native.ExitWindowsEx(Native.EWX_SHUTDOWN | Native.EWX_POWEROFF, Native.SHTDN_REASON_MAJOR_OTHER | Native.SHTDN_REASON_FLAG_PLANNED);
        }
        return false;
    }

    /// <summary>ExitWindowsEx требует SE_SHUTDOWN_NAME (документировано в описании функции).</summary>
    private static bool EnableShutdownPrivilege()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out IntPtr token))
            return false;
        try
        {
            if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out Native.LUID luid))
                return false;
            var tp = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Native.SE_PRIVILEGE_ENABLED };
            if (!Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;
            return Marshal.GetLastWin32Error() == 0;
        }
        finally { Native.CloseHandle(token); }
    }
}
