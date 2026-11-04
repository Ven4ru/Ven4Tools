using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Start;

/// <summary>Одна программа из shell:AppsFolder.</summary>
public sealed record AppEntry(string Name, string ParsingName)
{
    public override string ToString() => Name;
}

/// <summary>
/// Список программ через FOLDERID_AppsFolder + IShellItem/IEnumShellItems
/// (документированные интерфейсы оболочки, объявлены здесь же).
/// </summary>
internal static class ShellApps
{
    // ---- COM-интерфейсы (shobjidl_core.h) ----
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem? rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone([MarshalAs(UnmanagedType.Interface)] out IEnumShellItems ppenum);
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetKnownFolderItem([In] ref Guid rfid, uint flags, IntPtr hToken, [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");

    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Start");
    private static readonly string CacheFile = Path.Combine(CacheDir, "apps.json");

    /// <summary>Читает список с диска (быстрый первый показ). Пусто — если кэша нет.</summary>
    public static List<AppEntry> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                // Кэш мог быть испорчен или поправлен руками: записи без имени и повторы идентификатора
                // роняли меню при старте (null в поиске, повтор ключа в словаре).
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<AppEntry>();
                foreach (var a in JsonSerializer.Deserialize<List<AppEntry?>>(File.ReadAllText(CacheFile)) ?? new())
                    if (a != null && !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.ParsingName) && seen.Add(a.ParsingName))
                        list.Add(a);
                return list;
            }
        }
        catch (Exception ex) { App.Log("кэш списка программ не прочитан — перечитаю", ex); }
        return new();
    }

    private static void SaveCache(List<AppEntry> list)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) { App.Log("кэш списка программ не сохранён", ex); }
    }

    /// <summary>Перечитывает AppsFolder в отдельном STA-потоке (COM оболочки требует STA).</summary>
    public static Task<List<AppEntry>> EnumerateAsync()
    {
        var tcs = new TaskCompletionSource<List<AppEntry>>();
        var t = new Thread(() =>
        {
            try
            {
                var list = Enumerate();
                SaveCache(list);
                tcs.SetResult(list);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "Ven4Start.AppsFolder" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static List<AppEntry> Enumerate()
    {
        var result = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Guid folderId = FOLDERID_AppsFolder, iidItem = IID_IShellItem;
        SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out IShellItem folder);

        Guid bhid = BHID_EnumItems, iidEnum = IID_IEnumShellItems;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out IntPtr pEnum);
        var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(pEnum);
        Marshal.Release(pEnum);

        try
        {
            while (en.Next(1, out IShellItem? item, out uint fetched) == 0 && fetched == 1 && item != null)
            {
                try
                {
                    string? name = GetName(item, SIGDN_NORMALDISPLAY);
                    string? parsing = GetName(item, SIGDN_PARENTRELATIVEPARSING);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(parsing) && seen.Add(parsing))
                        result.Add(new AppEntry(name, parsing));
                }
                catch { /* пропускаем элемент, который не отдал имя */ }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(en);
            Marshal.ReleaseComObject(folder);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static string? GetName(IShellItem item, uint sigdn)
    {
        item.GetDisplayName(sigdn, out IntPtr p);
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Запуск: ShellExecute "shell:AppsFolder\&lt;AppUserModelID или путь&gt;".</summary>
    public static bool Launch(AppEntry app)
    {
        IntPtr r = Native.ShellExecute(IntPtr.Zero, null, @"shell:AppsFolder\" + app.ParsingName, null, null, Native.SW_SHOWNORMAL);
        return r.ToInt64() > 32;
    }
}
