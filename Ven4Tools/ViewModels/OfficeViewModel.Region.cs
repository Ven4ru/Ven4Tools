using System;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class OfficeViewModel
    {
        // ── Отображение региона (читаем реестр напрямую — изменения видны сразу) ──

        private void UpdateRegionDisplay()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Windows GeoID — читаем прямо из реестра, чтобы изменения были видны сразу
                try
                {
                    using var geo = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\Geo");
                    string? name   = geo?.GetValue("Name")?.ToString();
                    string? nation = geo?.GetValue("Nation")?.ToString();
                    RegionGeoText = (name, nation) switch
                    {
                        ({ } n, { } id) => $"{n} (GeoID: {id})",
                        ({ } n, _)      => n,
                        (_, { } id)     => $"GeoID: {id}",
                        _               => "недоступен"
                    };
                }
                catch { RegionGeoText = "ошибка чтения"; }

                // Office CountryCode
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Office\16.0\Common\ExperimentConfigs\Ecs");
                    string? raw = key?.GetValue("CountryCode")?.ToString();
                    RegionCCText = raw == null
                        ? "не задан"
                        : raw.StartsWith("std::wstring|") ? raw["std::wstring|".Length..] : raw;
                }
                catch { RegionCCText = "недоступен"; }
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
            // чтобы при аварийном завершении его можно было восстановить при следующем
            // запуске. Сама работа с маркером — в OfficeRegionRecoveryService: вкладка
            // создаётся лениво, поэтому восстановление не может жить в её конструкторе.
            OfficeRegionRecoveryService.Save(_originalOfficeCC, _originalGeoName, _originalGeoNation);
        }

        // Восстановление региона из persistent-маркера при открытии вкладки.
        // Основной вызов теперь при старте клиента (App), здесь остаётся как страховка
        // на случай, если маркер появился уже после старта, и ради обновления полей UI.
        private void RecoverRegionFromBackup()
        {
            if (OfficeRegionRecoveryService.Recover())
                UpdateRegionDisplay();
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

            // Регион восстановлен — удаляем persistent-маркер, он больше не нужен.
            OfficeRegionRecoveryService.Delete();

            UpdateRegionDisplay();
        }
    }
}
