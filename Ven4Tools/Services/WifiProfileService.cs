using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Ven4Tools.Services
{
    /// <summary>Сохранённый в Windows профиль Wi-Fi (без пароля — он читается отдельно по кнопке).</summary>
    public sealed record WifiProfileInfo(Guid InterfaceId, string ProfileName);

    /// <summary>Разобранный профиль: имя сети, тип защиты, пароль (если Windows его отдала).</summary>
    public sealed record WifiProfileDetails(string Ssid, WifiSecurityKind Security, string? Password, bool Hidden, bool KeyProtected);

    /// <summary>
    /// Сохранённые сети Wi-Fi через штатный Native Wifi API (wlanapi.dll).
    /// Выбран API, а не разбор вывода <c>netsh wlan show profile … key=clear</c>
    /// (как в наборе переустановки, <see cref="ReinstallKit"/>): вывод netsh переведён на
    /// язык системы («Содержимое ключа», «Key Content» …), и разбор ломался бы на каждой
    /// локализации. XML профиля от API одинаков везде.
    /// Пароль Windows отдаёт открытым текстом только процессу администратора
    /// (флаг WLAN_PROFILE_GET_PLAINTEXT_KEY) — клиент и так работает с этими правами.
    /// Пароли нигде не сохраняются и не пишутся в журнал.
    /// </summary>
    public static class WifiProfileService
    {
        private const uint ClientVersion = 2;               // Vista и новее
        private const uint WlanProfileGetPlaintextKey = 4;
        private const string ProfileNs = "http://www.microsoft.com/networking/WLAN/profile/v1";

        /// <summary>
        /// Есть ли в системе беспроводной адаптер. false — и при отсутствии службы
        /// WLAN AutoConfig / библиотеки wlanapi (серверные выпуски, ВМ без Wi-Fi).
        /// </summary>
        public static bool HasWirelessAdapter()
        {
            try
            {
                return WithHandle(h => EnumInterfaces(h).Count > 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>Список сохранённых профилей всех адаптеров (повторы по имени убраны).</summary>
        public static IReadOnlyList<WifiProfileInfo> GetProfiles()
        {
            try
            {
                return WithHandle(h =>
                {
                    var result = new List<WifiProfileInfo>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Guid iface in EnumInterfaces(h))
                    {
                        Guid id = iface;
                        if (WlanGetProfileList(h, ref id, IntPtr.Zero, out IntPtr list) != 0) continue;
                        try
                        {
                            int count = Marshal.ReadInt32(list);
                            const int itemSize = 512 + 4; // WCHAR[256] + DWORD flags
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr item = list + 8 + i * itemSize;
                                string name = Marshal.PtrToStringUni(item) ?? "";
                                if (name.Length > 0 && seen.Add(name))
                                    result.Add(new WifiProfileInfo(id, name));
                            }
                        }
                        finally { WlanFreeMemory(list); }
                    }
                    return (IReadOnlyList<WifiProfileInfo>)result;
                }) ?? Array.Empty<WifiProfileInfo>();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return Array.Empty<WifiProfileInfo>();
            }
        }

        /// <summary>
        /// Прочитать профиль вместе с паролем. null — профиль не найден или Windows отказала.
        /// </summary>
        public static WifiProfileDetails? GetDetails(WifiProfileInfo profile)
        {
            return WithHandle(h =>
            {
                Guid id = profile.InterfaceId;
                uint flags = WlanProfileGetPlaintextKey;
                uint rc = WlanGetProfile(h, ref id, profile.ProfileName, IntPtr.Zero, out IntPtr xmlPtr, ref flags, out _);
                if (rc != 0)
                {
                    AppLogger.Write($"[Wi-Fi] Профиль не прочитан, код {rc}");
                    return null;
                }
                try
                {
                    string xml = Marshal.PtrToStringUni(xmlPtr) ?? "";
                    return ParseProfileXml(xml);
                }
                finally { WlanFreeMemory(xmlPtr); }
            });
        }

        /// <summary>Разбор XML профиля WLAN. Чистая функция — покрыта юнит-тестами.</summary>
        public static WifiProfileDetails? ParseProfileXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (System.Xml.XmlException) { return null; }

            XNamespace ns = ProfileNs;
            XElement? root = doc.Root;
            if (root == null) return null;

            XElement? ssidEl = root.Element(ns + "SSIDConfig")?.Element(ns + "SSID");
            string? ssid = ssidEl?.Element(ns + "name")?.Value;
            // Имя может отсутствовать, если байты SSID не текст, — тогда собираем из hex.
            if (string.IsNullOrEmpty(ssid) && ssidEl?.Element(ns + "hex")?.Value is { Length: > 0 } hex)
                ssid = DecodeHexSsid(hex);
            ssid ??= root.Element(ns + "name")?.Value ?? "";

            bool hidden = string.Equals(root.Element(ns + "SSIDConfig")?.Element(ns + "nonBroadcast")?.Value,
                "true", StringComparison.OrdinalIgnoreCase);

            XElement? security = root.Element(ns + "MSM")?.Element(ns + "security");
            XElement? authEnc = security?.Element(ns + "authEncryption");
            string auth = authEnc?.Element(ns + "authentication")?.Value ?? "";
            string enc = authEnc?.Element(ns + "encryption")?.Value ?? "";
            WifiSecurityKind kind = MapSecurity(auth, enc);

            XElement? sharedKey = security?.Element(ns + "sharedKey");
            string? key = sharedKey?.Element(ns + "keyMaterial")?.Value;
            bool keyProtected = string.Equals(sharedKey?.Element(ns + "protected")?.Value, "true",
                StringComparison.OrdinalIgnoreCase);
            // Зашифрованный ключ — это не пароль, а двоичный блоб DPAPI: показывать его нельзя.
            string? password = keyProtected || string.IsNullOrEmpty(key) ? null : key;
            return new WifiProfileDetails(ssid, kind, password, hidden, keyProtected);
        }

        /// <summary>Тип защиты из полей authentication/encryption профиля.</summary>
        public static WifiSecurityKind MapSecurity(string authentication, string encryption)
        {
            string a = authentication.Trim().ToUpperInvariant();
            return a switch
            {
                "OPEN" => encryption.Trim().Equals("WEP", StringComparison.OrdinalIgnoreCase)
                    ? WifiSecurityKind.Wep : WifiSecurityKind.Open,
                "OWE" => WifiSecurityKind.Open,
                "SHARED" => WifiSecurityKind.Wep,
                "WPAPSK" or "WPA2PSK" or "WPA3SAE" => WifiSecurityKind.Wpa,
                "WPA" or "WPA2" or "WPA3" or "WPA3ENT" or "WPA3ENT192" => WifiSecurityKind.Enterprise,
                _ => WifiSecurityKind.Unknown,
            };
        }

        private static string? DecodeHexSsid(string hex)
        {
            if (hex.Length % 2 != 0) return null;
            try { return Encoding.UTF8.GetString(Convert.FromHexString(hex)); }
            catch (FormatException) { return null; }
        }

        // ── Native Wifi ──────────────────────────────────────────────────────

        private static T? WithHandle<T>(Func<IntPtr, T> action)
        {
            uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out IntPtr handle);
            if (rc != 0)
            {
                // 1062 — служба WLAN AutoConfig не запущена: адаптера нет или он отключён.
                if (rc != 1062) AppLogger.Write($"[Wi-Fi] WlanOpenHandle: код {rc}");
                return default;
            }
            try { return action(handle); }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }

        private static List<Guid> EnumInterfaces(IntPtr handle)
        {
            var result = new List<Guid>();
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list) != 0) return result;
            try
            {
                int count = Marshal.ReadInt32(list);
                const int itemSize = 16 + 512 + 4; // GUID + WCHAR[256] + состояние
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = list + 8 + i * itemSize;
                    result.Add(Marshal.PtrToStructure<Guid>(item));
                }
            }
            finally { WlanFreeMemory(list); }
            return result.Distinct().ToList();
        }

        [DllImport("wlanapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);

        [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WlanGetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid, string strProfileName,
            IntPtr pReserved, out IntPtr pstrProfileXml, ref uint pdwFlags, out uint pdwGrantedAccess);

        [DllImport("wlanapi.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void WlanFreeMemory(IntPtr pMemory);
    }
}
