using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class OfficeTab : UserControl
    {
        // ── Отображение региона (читаем реестр напрямую — изменения видны сразу) ──

        private void UpdateRegionDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                // Windows GeoID — читаем прямо из реестра, чтобы изменения были видны сразу
                try
                {
                    using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                    string? name   = geo?.GetValue("Name")?.ToString();
                    string? nation = geo?.GetValue("Nation")?.ToString();
                    txtRegionGeo.Text = (name, nation) switch
                    {
                        ({ } n, { } id) => $"{n} (GeoID: {id})",
                        ({ } n, _)      => n,
                        (_, { } id)     => $"GeoID: {id}",
                        _               => "недоступен"
                    };
                }
                catch { txtRegionGeo.Text = "ошибка чтения"; }

                // Office CountryCode
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                    string? raw = key?.GetValue("CountryCode")?.ToString();
                    txtRegionCC.Text = raw == null
                        ? "не задан"
                        : raw.StartsWith("std::wstring|") ? raw["std::wstring|".Length..] : raw;
                }
                catch { txtRegionCC.Text = "недоступен"; }
            });
        }

        // ── Сохранение / смена / восстановление региона ───────────────────────

        private void SaveRegion()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                _originalOfficeCC = key?.GetValue("CountryCode")?.ToString();
            }
            catch { _originalOfficeCC = null; }

            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                _originalGeoName   = geo?.GetValue("Name")?.ToString();
                _originalGeoNation = geo?.GetValue("Nation")?.ToString();
            }
            catch { _originalGeoName = _originalGeoNation = null; }

            // Persistent-маркер: сохраняем исходный регион на диск ДО SetRegionUS(),
            // чтобы при аварийном завершении его можно было восстановить при следующем запуске.
            try
            {
                var backup = new RegionBackup
                {
                    OfficeCC   = _originalOfficeCC,
                    GeoName    = _originalGeoName,
                    GeoNation  = _originalGeoNation
                };
                // Атомарная запись (temp+rename): именно этот маркер должен пережить
                // hard-kill/обрыв питания — обрыв посреди голого WriteAllText оставил бы
                // битый файл и лишил бы RecoverRegionFromBackup возможности восстановить регион.
                FileHelper.WriteAllTextAtomic(_regionBackupPath, JsonConvert.SerializeObject(backup));
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Сохранение маркера региона: {ex.Message}"); }
        }

        // Восстановление региона из persistent-маркера при старте (после hard-kill).
        private void RecoverRegionFromBackup()
        {
            try
            {
                if (!File.Exists(_regionBackupPath)) return;

                var backup = JsonConvert.DeserializeObject<RegionBackup>(File.ReadAllText(_regionBackupPath));
                if (backup == null)
                {
                    try { File.Delete(_regionBackupPath); } catch { }
                    return;
                }

                // Office CountryCode — те же ключи, что и в RestoreRegion()
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", writable: true);
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
                    using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
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

                try { File.Delete(_regionBackupPath); } catch { }
                AppLogger.Write("🔁 Регион восстановлен после аварийного завершения предыдущей установки Office");
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление региона из маркера: {ex.Message}"); }
        }

        // Валидация значений региона из region_backup.json перед записью в реестр.
        // Допускаются только буквы, цифры, пробелы и безопасные разделители (включая
        // формат Office CountryCode вида "std::wstring|US"). Макс. длина — 100 символов.
        private static bool IsValidRegionValue(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.Length <= 100
                && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[\w\s\-.,:|]+$");
        }

        // Модель persistent-маркера региона (region_backup.json). Поля могут быть null.
        private sealed class RegionBackup
        {
            public string? OfficeCC   { get; set; }
            public string? GeoName    { get; set; }
            public string? GeoNation  { get; set; }
        }

        private void SetRegionUS()
        {
            // Office ExperimentConfigs CountryCode
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                key?.SetValue("CountryCode", "std::wstring|US", RegistryValueKind.String);
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Office CountryCode: {ex.Message}"); }

            // Windows GeoID (Name = код ISO-3166 alpha-2, Nation = числовой GeoID)
            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                if (geo != null)
                {
                    geo.SetValue("Name",   "US",  RegistryValueKind.String);
                    geo.SetValue("Nation", "244", RegistryValueKind.String);
                }
                else
                    AppLogger.Write("⚠️ Control Panel\\International\\Geo — ключ не найден");
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Windows GeoID: {ex.Message}"); }

            UpdateRegionDisplay();
        }

        private void RestoreRegion()
        {
            // Office CountryCode
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs", writable: true);
                if (key != null)
                {
                    if (_originalOfficeCC != null)
                        key.SetValue("CountryCode", _originalOfficeCC, RegistryValueKind.String);
                    else
                        key.DeleteValue("CountryCode", throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление Office CC: {ex.Message}"); }

            // Windows GeoID
            try
            {
                using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo", writable: true);
                if (geo != null)
                {
                    if (_originalGeoName != null)
                        geo.SetValue("Name", _originalGeoName, RegistryValueKind.String);
                    else
                        geo.DeleteValue("Name", throwOnMissingValue: false);

                    if (_originalGeoNation != null)
                        geo.SetValue("Nation", _originalGeoNation, RegistryValueKind.String);
                    else
                        geo.DeleteValue("Nation", throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Восстановление Windows GeoID: {ex.Message}"); }

            // Регистр восстановлен — удаляем persistent-маркер, он больше не нужен.
            try { if (File.Exists(_regionBackupPath)) File.Delete(_regionBackupPath); } catch { }

            UpdateRegionDisplay();
        }
    }
}
