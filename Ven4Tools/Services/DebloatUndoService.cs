using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>Что твик «Очистки» меняет в системе — и, значит, что можно вернуть.</summary>
    public sealed record DebloatRegistryChange(string Path, string Name, int Value);

    /// <summary>Доступ к реестру и службам; вынесен интерфейсом ради тестов.</summary>
    public interface IDebloatSystemState
    {
        (bool Exists, int Value) ReadDword(string path, string name);
        bool WriteDword(string path, string name, int value);
        bool DeleteValue(string path, string name);
        /// <summary>Режим запуска службы из реестра: 2 — авто, 3 — вручную, 4 — отключена; null — службы нет.</summary>
        int? ReadServiceStartMode(string service);
        Task<bool> SetServiceStartModeAsync(string service, int mode, CancellationToken ct);
    }

    /// <summary>
    /// Точечный откат твиков «Очистки».
    ///
    /// Перед применением твика запоминается то, что он собирается изменить: прежние
    /// значения реестра (или их отсутствие) и режим запуска служб. Позже каждый такой
    /// твик можно вернуть по отдельности, не откатывая систему точкой восстановления
    /// целиком. Запоминается состояние ДО первого применения: повторное «Применить»
    /// его не перезаписывает, иначе «прежним» стало бы уже изменённое значение.
    ///
    /// Удаление встроенных приложений так не возвращается — пакет удалён, и вернуть
    /// его можно только из Microsoft Store.
    /// </summary>
    public sealed class DebloatUndoService
    {
        public static DebloatUndoService Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "debloat_undo.json"),
            new WindowsSystemState());

        private readonly string _path;
        private readonly IDebloatSystemState _system;
        private readonly object _gate = new();

        public DebloatUndoService(string path, IDebloatSystemState system)
        {
            _path = path;
            _system = system;
        }

        public sealed class RegistryState
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Existed { get; set; }
            public int Value { get; set; }
        }

        public sealed class ServiceState
        {
            public string Name { get; set; } = "";
            public int StartMode { get; set; }
        }

        public sealed class UndoRecord
        {
            public DateTime AppliedUtc { get; set; }
            public List<RegistryState> Registry { get; set; } = new();
            public List<ServiceState> Services { get; set; } = new();
        }

        /// <summary>Твики, для которых сохранено прежнее состояние.</summary>
        public IReadOnlyCollection<string> RecordedTweaks()
        {
            lock (_gate) return Load().Keys.ToList();
        }

        public bool CanUndo(string tweakId)
        {
            lock (_gate) return Load().ContainsKey(tweakId);
        }

        /// <summary>
        /// Запоминает состояние перед применением твика. Ничего не делает, если запись
        /// уже есть или твику нечего возвращать (удаление приложения).
        /// </summary>
        public void Capture(string tweakId, IReadOnlyList<DebloatRegistryChange> registry, string? service)
        {
            if (registry.Count == 0 && service == null) return;

            lock (_gate)
            {
                var all = Load();
                if (all.ContainsKey(tweakId)) return;

                var record = new UndoRecord { AppliedUtc = DateTime.UtcNow };
                foreach (var change in registry)
                {
                    var (exists, value) = _system.ReadDword(change.Path, change.Name);
                    record.Registry.Add(new RegistryState { Path = change.Path, Name = change.Name, Existed = exists, Value = value });
                }
                if (service != null && _system.ReadServiceStartMode(service) is { } mode)
                    record.Services.Add(new ServiceState { Name = service, StartMode = mode });

                // Службы нет в системе, а значений реестра твик не трогает — возвращать нечего.
                if (record.Registry.Count == 0 && record.Services.Count == 0) return;

                all[tweakId] = record;
                Save(all);
            }
        }

        /// <summary>
        /// Возвращает то, что было до твика. Запись убирается только при полном успехе:
        /// после частичного отката кнопку «Вернуть» можно нажать ещё раз.
        /// </summary>
        public async Task<bool> UndoAsync(string tweakId, CancellationToken ct = default)
        {
            UndoRecord? record;
            lock (_gate) Load().TryGetValue(tweakId, out record);
            if (record == null) return false;

            bool ok = true;
            foreach (var state in record.Registry)
            {
                ok &= state.Existed
                    ? _system.WriteDword(state.Path, state.Name, state.Value)
                    : _system.DeleteValue(state.Path, state.Name);
            }
            foreach (var state in record.Services)
                ok &= await _system.SetServiceStartModeAsync(state.Name, state.StartMode, ct);

            if (ok)
            {
                lock (_gate)
                {
                    var all = Load();
                    all.Remove(tweakId);
                    Save(all);
                }
            }
            return ok;
        }

        private Dictionary<string, UndoRecord> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
                var data = JsonSerializer.Deserialize<Dictionary<string, UndoRecord>>(File.ReadAllText(_path));
                return data == null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, UndoRecord>(
                        data.Where(pair => pair.Value != null), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Очистка] Записи отката не прочитаны: {ex.Message}");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save(Dictionary<string, UndoRecord> all)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                FileHelper.WriteAllTextAtomic(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Очистка] Записи отката не сохранены: {ex.Message}");
            }
        }

        // ── Настоящая система ────────────────────────────────────────────────────

        private sealed class WindowsSystemState : IDebloatSystemState
        {
            public (bool Exists, int Value) ReadDword(string path, string name)
            {
                try
                {
                    using var key = Open(path, writable: false);
                    return key?.GetValue(name) is int value ? (true, value) : (false, 0);
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Очистка] Не прочитано значение реестра {name}: {ex.Message}");
                    return (false, 0);
                }
            }

            public bool WriteDword(string path, string name, int value)
            {
                try
                {
                    if (!TrySplit(path, out var hive, out string subKey)) return false;
                    using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = root.CreateSubKey(subKey, writable: true);
                    key.SetValue(name, value, RegistryValueKind.DWord);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Очистка] Не записано значение реестра {name}: {ex.Message}");
                    return false;
                }
            }

            public bool DeleteValue(string path, string name)
            {
                try
                {
                    using var key = Open(path, writable: true);
                    key?.DeleteValue(name, throwOnMissingValue: false);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Очистка] Не удалено значение реестра {name}: {ex.Message}");
                    return false;
                }
            }

            public int? ReadServiceStartMode(string service)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
                    return key?.GetValue("Start") is int mode ? mode : null;
                }
                catch { return null; }
            }

            public Task<bool> SetServiceStartModeAsync(string service, int mode, CancellationToken ct) =>
                DebloatTweakExecutor.RestoreServiceAsync(service, mode, ct);

            private static RegistryKey? Open(string path, bool writable)
            {
                if (!TrySplit(path, out var hive, out string subKey)) return null;
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                return root.OpenSubKey(subKey, writable);
            }

            // Пути в описаниях твиков записаны как у PowerShell: «HKLM:\…», «HKCU:\…».
            private static bool TrySplit(string path, out RegistryHive hive, out string subKey)
            {
                hive = default;
                subKey = "";
                if (path.StartsWith(@"HKLM:\", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.LocalMachine;
                else if (path.StartsWith(@"HKCU:\", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.CurrentUser;
                else return false;
                subKey = path.Substring(6);
                return subKey.Length > 0;
            }
        }
    }
}
