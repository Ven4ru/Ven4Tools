using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Persistent-маркер исходного региона Windows/Office и его восстановление.
    ///
    /// Вкладка «Office» на время загрузки установщика подменяет регион пользователя
    /// (HKCU\Control Panel\International\Geo и Office CountryCode) и возвращает его
    /// обратно в finally. Маркер на диске — страховка на случай, когда finally не
    /// отработал: hard-kill, обрыв питания, падение процесса.
    ///
    /// Логика вынесена из code-behind вкладки намеренно: раньше восстановление
    /// вызывалось только из конструктора OfficeTab, а вкладка создаётся ЛЕНИВО —
    /// при первом переходе на неё. То есть страховка срабатывала лишь если
    /// пользователь снова открывал именно ту вкладку, на которой всё сломалось;
    /// без интернета кнопка вкладки вообще скрыта, и подменённый регион оставался
    /// в системе бессрочно. Теперь восстановление выполняется при старте клиента.
    /// </summary>
    public static class OfficeRegionRecoveryService
    {
        private const string OfficeEcsKey = @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs";
        private const string GeoKey = @"Control Panel\International\Geo";

        public static readonly string BackupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "region_backup.json");

        /// <summary>Модель маркера (region_backup.json). Поля могут быть null.</summary>
        public sealed class RegionBackup
        {
            public string? OfficeCC  { get; set; }
            public string? GeoName   { get; set; }
            public string? GeoNation { get; set; }
        }

        /// <summary>
        /// Сохраняет исходные значения региона на диск ДО подмены. Запись атомарная
        /// (temp+rename): именно этот маркер должен пережить hard-kill, а обрыв посреди
        /// голого WriteAllText оставил бы битый файл и лишил бы восстановление смысла.
        /// </summary>
        public static void Save(string? officeCC, string? geoName, string? geoNation)
        {
            try
            {
                var backup = new RegionBackup
                {
                    OfficeCC  = officeCC,
                    GeoName   = geoName,
                    GeoNation = geoNation
                };
                FileHelper.WriteAllTextAtomic(BackupPath, JsonConvert.SerializeObject(backup));
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Сохранение маркера региона: {ex.Message}"); }
        }

        /// <summary>Удаляет маркер: регион уже возвращён штатным путём.</summary>
        public static void Delete()
        {
            try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
        }

        /// <summary>
        /// Восстанавливает регион из маркера, если он остался с прошлого сеанса.
        /// Возвращает true, если восстановление выполнялось. Best-effort — сбой не
        /// должен мешать старту приложения.
        /// </summary>
        public static bool Recover()
        {
            try
            {
                if (!File.Exists(BackupPath)) return false;

                var backup = JsonConvert.DeserializeObject<RegionBackup>(File.ReadAllText(BackupPath));
                if (backup == null)
                {
                    Delete();
                    return false;
                }

                // Office CountryCode — те же ключи, что и при штатном восстановлении.
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(OfficeEcsKey, writable: true);
                    if (key != null)
                    {
                        if (backup.OfficeCC != null)
                        {
                            if (IsValidRegionValue(backup.OfficeCC))
                                key.SetValue("CountryCode", backup.OfficeCC, RegistryValueKind.String);
                            else
                                AppLogger.Write($"Невалидное значение региона (OfficeCC): {backup.OfficeCC}");
                        }
                        else
                            key.DeleteValue("CountryCode", throwOnMissingValue: false);
                    }
                }
                catch { /* ключа может не быть — игнорируем */ }

                // Windows GeoID
                try
                {
                    using var geo = Registry.CurrentUser.OpenSubKey(GeoKey, writable: true);
                    if (geo != null)
                    {
                        if (backup.GeoName != null)
                        {
                            if (IsValidRegionValue(backup.GeoName))
                                geo.SetValue("Name", backup.GeoName, RegistryValueKind.String);
                            else
                                AppLogger.Write($"Невалидное значение региона (GeoName): {backup.GeoName}");
                        }
                        else
                            geo.DeleteValue("Name", throwOnMissingValue: false);

                        if (backup.GeoNation != null)
                        {
                            if (IsValidRegionValue(backup.GeoNation))
                                geo.SetValue("Nation", backup.GeoNation, RegistryValueKind.String);
                            else
                                AppLogger.Write($"Невалидное значение региона (GeoNation): {backup.GeoNation}");
                        }
                        else
                            geo.DeleteValue("Nation", throwOnMissingValue: false);
                    }
                }
                catch { /* игнорируем */ }

                Delete();
                AppLogger.Write("🔁 Регион восстановлен после аварийного завершения предыдущей установки Office");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"⚠️ Восстановление региона из маркера: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Валидация значений региона из маркера перед записью в реестр: файл лежит в
        /// user-writable папке, а клиент работает elevated. Допускаются только буквы,
        /// цифры, пробелы и безопасные разделители (включая формат Office CountryCode
        /// вида «std::wstring|US»). Максимальная длина — 100 символов.
        /// </summary>
        internal static bool IsValidRegionValue(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.Length <= 100
                && Regex.IsMatch(value, @"^[\w\s\-.,:|]+$");
        }
    }
}
