using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Хранилище альтернативных источников установки (alternatives.json): для приложения
    /// каталога можно задать свой winget-идентификатор и/или прямую ссылку, и указать,
    /// должны ли они иметь приоритет над источниками из каталога.
    ///
    /// Выделено из AppManager: раньше тот держал под одним замком три несвязанных
    /// хранилища сразу (альтернативы, скрытые приложения, пользовательские приложения),
    /// и любое изменение одного требовало читать код двух других. Здесь хранилище знает
    /// только про свой файл и свой словарь; список приложений оно не хранит — применение
    /// записей к конкретному <see cref="AppInfo"/> вынесено в <see cref="ApplyOverride"/>,
    /// которую вызывает владелец списка.
    /// </summary>
    public sealed class AlternativeSourceStore
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private Dictionary<string, AlternativeSource> _entries = new();

        public AlternativeSourceStore(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Перечитывает файл. Если файла нет — уже загруженные записи сохраняются
        /// (так же вело себя исходное чтение в AppManager).
        /// </summary>
        public void Reload()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var json = File.ReadAllText(_path);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, AlternativeSource>>(json)
                    ?? new Dictionary<string, AlternativeSource>();

                lock (_lock) { _entries = loaded; }
            }
            catch (Exception ex) { AppLogger.Write($"[AlternativeSourceStore] Reload: {ex.Message}"); }
        }

        /// <summary>Применяет сохранённые записи к переданным приложениям.</summary>
        public void ApplyToApps(IReadOnlyCollection<AppInfo> apps)
        {
            if (apps == null || apps.Count == 0) return;

            try
            {
                List<KeyValuePair<string, AlternativeSource>> snapshot;
                lock (_lock) { snapshot = _entries.ToList(); }

                foreach (var kvp in snapshot)
                {
                    var app = apps.FirstOrDefault(a => a.Id == kvp.Key);
                    if (app == null) continue;
                    ApplyOverride(app, kvp.Value.WingetId, kvp.Value.Url, kvp.Value.UrlPriority);
                }
            }
            catch (Exception ex) { AppLogger.Write($"[AlternativeSourceStore] ApplyToApps: {ex.Message}"); }
        }

        /// <summary>
        /// Подменяет источники в загруженном каталоге: winget-идентификатор и ссылку
        /// на скачивание. В отличие от <see cref="ApplyToApps"/> здесь именно замена,
        /// а не добавление в список ссылок — у записи каталога ссылка одна.
        /// </summary>
        public void ApplyToCatalog(MasterCatalog catalog)
        {
            if (catalog?.Apps == null || catalog.Apps.Count == 0) return;

            lock (_lock)
            {
                foreach (var catalogApp in catalog.Apps)
                {
                    if (!_entries.TryGetValue(catalogApp.Id, out var alt)) continue;

                    if (!string.IsNullOrEmpty(alt.WingetId))
                        catalogApp.WingetId = alt.WingetId;
                    if (!string.IsNullOrEmpty(alt.Url))
                        catalogApp.DownloadUrl = alt.Url;
                }
            }
        }

        /// <summary>
        /// Записывает альтернативный источник и сохраняет файл. Непустые аргументы
        /// обновляют соответствующую часть записи, пустые — оставляют прежнюю.
        /// </summary>
        public void Set(string appId, string? wingetId, string? url, bool priority)
        {
            try
            {
                lock (_lock)
                {
                    if (!_entries.ContainsKey(appId))
                        _entries[appId] = new AlternativeSource();

                    if (!string.IsNullOrEmpty(wingetId))
                    {
                        _entries[appId].WingetId = wingetId;
                        _entries[appId].Priority = priority;
                        _entries[appId].LastUpdated = DateTime.Now;
                    }

                    if (!string.IsNullOrEmpty(url))
                    {
                        _entries[appId].Url = url;
                        _entries[appId].UrlPriority = priority;
                        _entries[appId].LastUpdated = DateTime.Now;
                    }
                }

                Save();
            }
            catch (Exception ex) { AppLogger.Write($"[AlternativeSourceStore] Set: {ex.Message}"); }
        }

        /// <summary>Удаляет запись и сохраняет файл. true — запись действительно была.</summary>
        public bool Remove(string appId)
        {
            try
            {
                bool removed;
                lock (_lock) { removed = _entries.Remove(appId); }
                if (removed) Save();
                return removed;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[AlternativeSourceStore] Remove: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Проецирует альтернативный источник на приложение: winget-идентификатор
        /// замещает прежний, ссылка добавляется в начало или конец списка установщиков
        /// в зависимости от приоритета (повторно та же ссылка не добавляется).
        /// </summary>
        public static void ApplyOverride(AppInfo app, string? wingetId, string? url, bool urlPriority)
        {
            if (!string.IsNullOrEmpty(wingetId))
                app.AlternativeId = wingetId;

            if (!string.IsNullOrEmpty(url) && !app.InstallerUrls.Contains(url))
            {
                if (urlPriority)
                    app.InstallerUrls.Insert(0, url);
                else
                    app.InstallerUrls.Add(url);
            }
        }

        private void Save()
        {
            try
            {
                string json;
                lock (_lock) { json = JsonConvert.SerializeObject(_entries, Formatting.Indented); }
                FileHelper.WriteAllTextAtomic(_path, json);
            }
            catch (Exception ex) { AppLogger.Write($"[AlternativeSourceStore] Save: {ex.Message}"); }
        }
    }
}
