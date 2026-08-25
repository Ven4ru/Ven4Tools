using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class InstalledViewModel
    {
        // ── Фильтрация ─────────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            string search = SearchText.Trim().ToLowerInvariant();

            IEnumerable<InstalledApp> filtered = _allApps;

            if (IsUnknownFilterSelected)
                filtered = filtered.Where(a => a.IsUnknownSource);

            if (OnlyUpdates)
                filtered = filtered.Where(a => a.HasUpdate);

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(a =>
                    a.Name.ToLowerInvariant().Contains(search) ||
                    a.WingetId.ToLowerInvariant().Contains(search));

            // Сортировка отображаемого списка
            filtered = SortIndex switch
            {
                1 => filtered.OrderBy(a => a.Version, StringComparer.OrdinalIgnoreCase),          // по версии
                2 => filtered.OrderByDescending(a => a.HasUpdate)                                 // сначала с обновлениями
                             .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)              // по имени
            };

            DisplayedApps = filtered.ToList();
            RecomputeStats();
            RecomputeSelectAllState();
        }

        private void RecomputeStats()
        {
            int total   = _allApps.Count;
            int updates = _allApps.Count(a => a.HasUpdate);
            int unknown = _allApps.Count(a => a.IsUnknownSource);
            StatsText = $"Всего: {total}  |  Обновлений: {updates}  |  Неизвестных: {unknown}";
        }

        private async Task RunRefreshAsync()
        {
            if (IsRefreshing || IsUpgradingAll) return;
            try
            {
                IsRefreshing = true;
                // Сброс кэша предзагрузки — "Обновить" всегда идёт напрямую в winget
                lock (_preloadLock)
                {
                    _preloadTask = null;
                    _cachedRawOutput = null;
                }
                await LoadAppsAsync();
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            finally { IsRefreshing = false; }
        }
    }
}
