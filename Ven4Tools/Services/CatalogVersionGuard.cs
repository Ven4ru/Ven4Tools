using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Хранилище последней применённой версии каталога (anti-rollback) в отдельном
    /// DPAPI-защищённом файле, привязанном к учётной записи Windows.
    ///
    /// Раньше значение лежало в profile.json — обычный JSON в user-writable
    /// %LocalAppData%\Ven4Tools без защиты: правка/удаление файла сбрасывала счётчик
    /// и снимала защиту от отката каталога. Навесить DPAPI на весь profile.json нельзя:
    /// он переносится между машинами экспортом настроек, а DPAPI-blob на другой машине
    /// не расшифруется. Anti-rollback — машинно-локальное состояние безопасности,
    /// роуминг ему не нужен, поэтому выносим его в собственный защищённый файл.
    /// </summary>
    public static class CatalogVersionGuard
    {
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "catalog_guard.dat");

        // Доп. энтропия DPAPI: привязывает blob именно к этому назначению.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ven4Tools.catalog_guard.v1");

        private static int? _cached;
        private static readonly object _lock = new();

        /// <summary>Последняя применённая версия каталога (0 — памяти ещё нет).</summary>
        public static int Load()
        {
            lock (_lock)
            {
                _cached ??= ReadFromDisk();
                return _cached.Value;
            }
        }

        /// <summary>Запоминает версию, если она строго выше уже сохранённой.</summary>
        public static void Save(int version)
        {
            lock (_lock)
            {
                int current = (_cached ??= ReadFromDisk());
                if (version <= current) return;
                // _cached обновляем ТОЛЬКО если запись реально удалась — иначе текущий
                // процесс считал бы версию сохранённой, а после перезапуска ReadFromDisk()
                // вернула бы старое значение (защита тихо откатилась бы, при этом ничего
                // не сообщив вызывающему коду).
                if (Persist(version))
                    _cached = version;
            }
        }

        private static int ReadFromDisk()
        {
            try
            {
                if (File.Exists(_path))
                    return TryUnprotect(File.ReadAllText(_path)) ?? 0;

                // Разовая миграция: наследуем значение из legacy-профиля, чтобы апгрейд
                // не обнулил уже накопленную защиту от отката.
                int legacy = ProfileService.Current.LastCatalogVersion;
                if (legacy > 0) { Persist(legacy); return legacy; }
            }
            catch (Exception ex) { AppLogger.Write($"[CatalogVersionGuard] Read: {ex.Message}"); }
            return 0;
        }

        private static bool Persist(int version)
        {
            try
            {
                FileHelper.WriteAllTextAtomic(_path, Protect(version));
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[CatalogVersionGuard] Persist: {ex.Message}");
                return false;
            }
        }

        internal static string Protect(int version)
        {
            var plain = Encoding.UTF8.GetBytes(version.ToString(CultureInfo.InvariantCulture));
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        internal static int? TryUnprotect(string raw)
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(raw.Trim());
                var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var text = Encoding.UTF8.GetString(plain);
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : (int?)null;
            }
            catch { return null; }
        }
    }
}
