using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Хранилище скрытых приложений (hidden.json): идентификаторы, которые не должны
    /// попадать в общий список каталога. Полностью независимо от остальных хранилищ —
    /// знает только про множество идентификаторов и свой файл.
    ///
    /// Выделено из AppManager вместе с двумя другими хранилищами, чтобы у каждого была
    /// своя ответственность и свой замок, а не один общий на три несвязанных состояния.
    /// </summary>
    public sealed class HiddenAppsStore
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private HashSet<string> _hidden = new();

        public HiddenAppsStore(string path)
        {
            _path = path;
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var json = File.ReadAllText(_path);
                var loaded = JsonConvert.DeserializeObject<HashSet<string>>(json) ?? new HashSet<string>();
                lock (_lock) { _hidden = loaded; }
            }
            catch (Exception ex) { AppLogger.Write($"[HiddenAppsStore] Load: {ex.Message}"); }
        }

        public void Save()
        {
            try
            {
                string json;
                lock (_lock) { json = JsonConvert.SerializeObject(_hidden, Formatting.Indented); }
                FileHelper.WriteAllTextAtomic(_path, json);
            }
            catch (Exception ex) { AppLogger.Write($"[HiddenAppsStore] Save: {ex.Message}"); }
        }

        public bool IsHidden(string appId)
        {
            lock (_lock) { return _hidden.Contains(appId); }
        }

        /// <summary>Копия множества — чтобы фильтровать список приложений вне замка.</summary>
        public HashSet<string> Snapshot()
        {
            lock (_lock) { return new HashSet<string>(_hidden); }
        }
    }
}
