using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class InstalledTab : UserControl
    {
        // ── Фильтрация ─────────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            if (lstApps == null) return;
            string search = txtSearch.Text.Trim().ToLowerInvariant();

            IEnumerable<InstalledApp> filtered = _allApps;

            if (rdbUnknown.IsChecked == true)
                filtered = filtered.Where(a => a.IsUnknownSource);

            if (chkOnlyUpdates?.IsChecked == true)
                filtered = filtered.Where(a => a.HasUpdate);

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(a =>
                    a.Name.ToLowerInvariant().Contains(search) ||
                    a.WingetId.ToLowerInvariant().Contains(search));

            // Сортировка отображаемого списка
            filtered = (cmbSort?.SelectedIndex ?? 0) switch
            {
                1 => filtered.OrderBy(a => a.Version, StringComparer.OrdinalIgnoreCase),          // по версии
                2 => filtered.OrderByDescending(a => a.HasUpdate)                                 // сначала с обновлениями
                             .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)              // по имени
            };

            lstApps.ItemsSource = filtered.ToList();
            UpdateStats();
            UpdateSelectAllState();
        }

        private void UpdateStats()
        {
            int total   = _allApps.Count;
            int updates = _allApps.Count(a => a.HasUpdate);
            int unknown = _allApps.Count(a => a.IsUnknownSource);
            txtStats.Text = $"Всего: {total}  |  Обновлений: {updates}  |  Неизвестных: {unknown}";
        }

        // ── Событие-обработчики ────────────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void FilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private void CmbSort_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                btnRefresh.IsEnabled = false;
                // Сброс кэша предзагрузки — "Обновить" всегда идёт напрямую в winget
                lock (_preloadLock)
                {
                    _preloadTask = null;
                    _cachedRawOutput = null;
                }
                await LoadAppsAsync();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { btnRefresh.IsEnabled = true; }
        }
    }
}
