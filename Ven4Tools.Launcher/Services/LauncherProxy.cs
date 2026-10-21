using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Режим прокси, выбранный пользователем.</summary>
    public enum ProxyMode
    {
        /// <summary>Как настроено в Windows (параметры браузера, PAC) — поведение по умолчанию.</summary>
        System,
        /// <summary>Адрес, логин и пароль заданы в лаунчере вручную.</summary>
        Manual
    }

    /// <summary>
    /// Настройки прокси лаунчера и сам прокси, через который ходят ВСЕ его HttpClient
    /// (см. <see cref="LauncherHttp"/>).
    ///
    /// Прокси «живой»: <see cref="Current"/> — один объект на весь процесс, который при
    /// каждом запросе смотрит на текущие настройки. Статические HttpClient сервисов
    /// создаются один раз за процесс, и без этого смена режима в окне настроек
    /// действовала бы только после перезапуска. SocketsHttpHandler спрашивает прокси
    /// заново на каждый запрос и держит пулы соединений отдельно для каждого адреса
    /// прокси, так что переключение не смешивает старые и новые соединения.
    ///
    /// Хранение: отдельный файл %LOCALAPPDATA%\Ven4Tools\Launcher\proxy.json, а не общий
    /// launcher_settings.json — пароль лежит только здесь и только зашифрованным DPAPI
    /// (CurrentUser): расшифровать его может лишь та же учётная запись Windows на том же ПК.
    ///
    /// Выключатели из подписанного version.json (объект "launcher_flags", поля
    /// proxy_manual / proxy_default_credentials / proxy_direct_fallback): отсутствие поля =
    /// ВКЛ. Последнее подтверждённое подписью значение сохраняется в тот же файл, чтобы
    /// выключатель действовал и в запуск, когда CDN недоступен.
    /// </summary>
    internal static class LauncherProxy
    {
        // Дополнительная «соль» DPAPI: зашифрованное лаунчером не расшифруется чужим
        // кодом, вызвавшим Unprotect без неё (от процессов того же пользователя не
        // защищает — это и не цель; цель — не хранить пароль открытым текстом).
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ven4Tools.Launcher.Proxy.v1");

        private static readonly object _lock = new();
        private static ProxySettingsFile _settings = new();
        private static string _path = DefaultPath;
        private static WebProxy? _manualProxy;
        private static bool _loaded;

        /// <summary>Штатное расположение: %LOCALAPPDATA%\Ven4Tools\Launcher\proxy.json.</summary>
        public static string DefaultPath => Path.Combine(LauncherInstallation.InstallDir, "proxy.json");

        /// <summary>Прокси для всех обработчиков лаунчера (кроме «CDN по прямому IP»).</summary>
        public static IWebProxy Current { get; } = new DynamicProxy();

        /// <summary>Режим, выбранный пользователем (без учёта выключателя из манифеста).</summary>
        public static ProxyMode Mode { get { EnsureLoaded(); lock (_lock) return _settings.Mode; } }

        /// <summary>Адрес ручного прокси, как его ввёл пользователь (или пусто).</summary>
        public static string ManualAddress { get { EnsureLoaded(); lock (_lock) return _settings.Address ?? ""; } }

        /// <summary>Логин ручного прокси (или пусто).</summary>
        public static string ManualUserName { get { EnsureLoaded(); lock (_lock) return _settings.UserName ?? ""; } }

        /// <summary>Сохранён ли пароль ручного прокси.</summary>
        public static bool HasManualPassword { get { EnsureLoaded(); lock (_lock) return !string.IsNullOrEmpty(_settings.PasswordProtected); } }

        /// <summary>Разрешён ли ручной режим подписанным манифестом (выключатель proxy_manual).</summary>
        public static bool ManualAllowed { get { EnsureLoaded(); lock (_lock) return _settings.FlagManual != false; } }

        /// <summary>Передавать ли учётные данные Windows системному прокси (proxy_default_credentials).</summary>
        public static bool DefaultCredentialsAllowed { get { EnsureLoaded(); lock (_lock) return _settings.FlagDefaultCredentials != false; } }

        /// <summary>Повторять ли запрос напрямую при ошибке прокси (proxy_direct_fallback).</summary>
        public static bool DirectFallbackAllowed { get { EnsureLoaded(); lock (_lock) return _settings.FlagDirectFallback != false; } }

        /// <summary>Действующий режим: ручной — только если он выбран, задан и не выключен манифестом.</summary>
        public static ProxyMode EffectiveMode
        {
            get
            {
                EnsureLoaded();
                lock (_lock)
                    return _settings.Mode == ProxyMode.Manual && _manualProxy != null && _settings.FlagManual != false
                        ? ProxyMode.Manual
                        : ProxyMode.System;
            }
        }

        /// <summary>
        /// Есть ли вообще прокси на пути к <paramref name="uri"/> (системный или ручной).
        /// Нужен, чтобы отличать ошибку прокси от ошибки самого сервера.
        /// </summary>
        public static bool IsProxied(Uri uri)
        {
            try
            {
                return !Current.IsBypassed(uri) && Current.GetProxy(uri) is { } p && p != uri;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Запоминает выключатели из подписанного version.json. Вызывается только для
        /// манифеста, прошедшего проверку подписи; null (нет объекта) = все ВКЛ.
        /// </summary>
        public static void ApplyManifestFlags(LauncherFlags? flags)
        {
            EnsureLoaded();
            bool changed;
            lock (_lock)
            {
                bool? manual = flags?.ProxyManual;
                bool? creds = flags?.ProxyDefaultCredentials;
                bool? direct = flags?.ProxyDirectFallback;
                changed = manual != _settings.FlagManual || creds != _settings.FlagDefaultCredentials ||
                          direct != _settings.FlagDirectFallback;
                _settings.FlagManual = manual;
                _settings.FlagDefaultCredentials = creds;
                _settings.FlagDirectFallback = direct;
            }
            if (changed)
            {
                LauncherLog.Write($"[Прокси] Выключатели из подписанного манифеста: ручной режим {OnOff(flags?.ProxyManual)}, " +
                                  $"учётные данные Windows {OnOff(flags?.ProxyDefaultCredentials)}, " +
                                  $"повтор напрямую {OnOff(flags?.ProxyDirectFallback)}");
                TrySave();
            }
        }

        private static string OnOff(bool? flag) => flag == false ? "выключено" : "включено";

        /// <summary>Переключение на «Системный». Ручная настройка остаётся в файле для повторного выбора.</summary>
        public static void UseSystem()
        {
            EnsureLoaded();
            lock (_lock) _settings.Mode = ProxyMode.System;
            TrySave();
            LauncherLog.Write("[Прокси] Режим: системный");
        }

        /// <summary>
        /// Сохраняет ручную настройку и включает её. Вызывать ТОЛЬКО после успешной
        /// проверки (<see cref="LauncherHttp.TestProxyAsync"/>): неверный прокси ломает
        /// все загрузки, включая самообновление лаунчера.
        /// </summary>
        /// <param name="password">null — оставить сохранённый пароль; пустая строка — удалить.</param>
        public static void SaveManual(Uri address, string? userName, string? password)
        {
            EnsureLoaded();
            lock (_lock)
            {
                _settings.Mode = ProxyMode.Manual;
                _settings.Address = address.ToString();
                _settings.UserName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
                if (password != null)
                    _settings.PasswordProtected = password.Length == 0 ? null : Protect(password);
                _manualProxy = BuildManualProxy(_settings);
            }
            TrySave();
            LauncherLog.Write("[Прокси] Режим: вручную (настройка проверена и сохранена)");
        }

        /// <summary>Сохранённый пароль ручного прокси (для повторной проверки в окне) или null.</summary>
        internal static string? GetSavedPassword()
        {
            EnsureLoaded();
            lock (_lock) return Unprotect(_settings.PasswordProtected);
        }

        /// <summary>
        /// Разбор введённого адреса прокси. Принимает «host:port», «http://host:port»;
        /// схема — только http (так прокси задаёт и Windows: HTTPS-трафик идёт через
        /// CONNECT по тому же адресу). Порт обязателен — угадывать его нельзя.
        /// Чистая функция без сети — покрыта unit-тестами.
        /// </summary>
        internal static bool TryParseAddress(string? text, out Uri? address)
        {
            address = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp) return false;
            if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            // Явный порт: Uri подставляет 80 при его отсутствии — проверяем по исходной строке.
            string authority = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
            if (uri.IsDefaultPort && !authority.EndsWith(":80", StringComparison.Ordinal)) return false;
            if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)) return false;
            address = new Uri($"http://{uri.Authority}/");
            return true;
        }

        /// <summary>Учётные данные для системного прокси (или null, если выключено манифестом).</summary>
        internal static ICredentials? SystemCredentials =>
            DefaultCredentialsAllowed ? CredentialCache.DefaultCredentials : null;

        // ---------- хранение ----------

        internal static void LoadFrom(string path)
        {
            lock (_lock)
            {
                _path = path;
                _settings = ReadFile(path);
                _manualProxy = BuildManualProxy(_settings);
                _loaded = true;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                _settings = ReadFile(_path);
                _manualProxy = BuildManualProxy(_settings);
                _loaded = true;
            }
        }

        private static ProxySettingsFile ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return new ProxySettingsFile();
                var s = JsonSerializer.Deserialize<ProxySettingsFile>(File.ReadAllText(path));
                if (s == null) return new ProxySettingsFile();
                if (!Enum.IsDefined(s.Mode)) s.Mode = ProxyMode.System;
                return s;
            }
            catch (Exception ex)
            {
                // Битый файл = «Системный», как было до появления настройки. Лаунчер
                // обязан выйти в сеть хотя бы так — иначе он не сможет и обновиться.
                LauncherLog.Write($"[Прокси] Не удалось прочитать настройки прокси, применён системный: {ex.Message}");
                return new ProxySettingsFile();
            }
        }

        private static void TrySave()
        {
            try
            {
                string json;
                lock (_lock)
                    json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                FileHelper.WriteAllTextAtomic(_path, json);
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Прокси] Не удалось сохранить настройки прокси: {ex.Message}");
            }
        }

        private static WebProxy? BuildManualProxy(ProxySettingsFile s)
        {
            if (!TryParseAddress(s.Address, out var address) || address == null) return null;
            return CreateWebProxy(address, s.UserName, Unprotect(s.PasswordProtected));
        }

        /// <summary>WebProxy для ручного режима (и для кнопки «Проверить» до сохранения).</summary>
        internal static WebProxy CreateWebProxy(Uri address, string? userName, string? password)
        {
            var proxy = new WebProxy(address) { BypassProxyOnLocal = true };
            if (!string.IsNullOrEmpty(userName))
                proxy.Credentials = new NetworkCredential(userName, password ?? "");
            return proxy;
        }

        private static string Protect(string password)
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        private static string? Unprotect(string? protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return null;
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                // Файл перенесён с другого ПК/учётной записи — пароль не расшифровать.
                LauncherLog.Write($"[Прокси] Сохранённый пароль прокси не расшифровывается (другая учётная запись?): {ex.GetType().Name}");
                return null;
            }
        }

        /// <summary>
        /// Прокси, который на каждый запрос выбирает между ручным и системным.
        /// </summary>
        private sealed class DynamicProxy : IWebProxy
        {
            public ICredentials? Credentials
            {
                get
                {
                    var manual = ManualOrNull();
                    return manual != null ? manual.Credentials : SystemCredentials;
                }
                // SocketsHttpHandler учётные данные сюда не пишет; пользовательский код — тоже.
                set { }
            }

            public Uri? GetProxy(Uri destination)
            {
                var manual = ManualOrNull();
                return manual != null ? manual.GetProxy(destination) : HttpClient.DefaultProxy.GetProxy(destination);
            }

            public bool IsBypassed(Uri host)
            {
                var manual = ManualOrNull();
                return manual != null ? manual.IsBypassed(host) : HttpClient.DefaultProxy.IsBypassed(host);
            }

            private static WebProxy? ManualOrNull()
            {
                EnsureLoaded();
                lock (_lock)
                    return _settings.Mode == ProxyMode.Manual && _settings.FlagManual != false ? _manualProxy : null;
            }
        }

        /// <summary>Содержимое proxy.json.</summary>
        internal sealed class ProxySettingsFile
        {
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public ProxyMode Mode { get; set; } = ProxyMode.System;
            public string? Address { get; set; }
            public string? UserName { get; set; }
            // DPAPI (CurrentUser) + base64. Открытым текстом пароль не хранится нигде.
            public string? PasswordProtected { get; set; }
            // Последние подтверждённые подписью выключатели (null = ВКЛ).
            public bool? FlagManual { get; set; }
            public bool? FlagDefaultCredentials { get; set; }
            public bool? FlagDirectFallback { get; set; }
        }
    }
}
