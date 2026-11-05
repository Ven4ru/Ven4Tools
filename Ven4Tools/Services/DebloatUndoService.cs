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

        /// <summary>Значение реестра как есть (вид и данные); null — значения нет. По умолчанию — только DWORD.</summary>
        (RegistryValueKind Kind, object Data)? ReadRaw(string path, string name)
        {
            var (exists, value) = ReadDword(path, name);
            return exists ? (RegistryValueKind.DWord, value) : null;
        }

        /// <summary>Записать значение с его видом (побайтный откат). По умолчанию — только DWORD.</summary>
        bool WriteRaw(string path, string name, RegistryValueKind kind, object data) =>
            kind == RegistryValueKind.DWord && data is int value && WriteDword(path, name, value);
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
            new WindowsSystemState(),
            RegistryOwnership.Default);

        private const string Section = "Очистка";
        private const string OwnerPrefix = "debloat:";

        private readonly string _path;
        private readonly IDebloatSystemState _system;
        private readonly RegistryOwnership? _owners;
        private readonly object _gate = new();

        /// <summary>Доступ к реестру и службам — тот же, через который идёт откат.</summary>
        internal IDebloatSystemState System => _system;

        /// <param name="owners">
        /// Общий учёт владения значениями с разделами панели задач; null — без него (каждый
        /// откат по своей записи, как раньше).
        /// </param>
        public DebloatUndoService(string path, IDebloatSystemState system, RegistryOwnership? owners = null)
        {
            _path = path;
            _system = system;
            _owners = owners;
        }

        private static string Owner(string tweakId) => OwnerPrefix + tweakId;

        /// <summary>
        /// Файл отката был испорчен и отложен, а отложенный файл ещё не разобран: пока он
        /// лежит, «Применить» твиков с откатом отказывает. null — всё в порядке.
        /// </summary>
        public string? BrokenBackup
        {
            get
            {
                lock (_gate)
                {
                    Load();   // испорченный файл откладывается при чтении
                    return UndoFileGuard.Broken(_path);
                }
            }
        }

        /// <summary>Текст отказа для журнала и строки состояния; null — отказа нет.</summary>
        public string? BrokenMessage => BrokenBackup is { } bad ? UndoFileGuard.BrokenMessage(bad) : null;

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
        ///
        /// false — применять твик нельзя: файл отката испорчен и отложен в <c>.bad</c> (пока
        /// его не разобрали) или сейчас не читается. Применить без записи значило бы потом
        /// снять «как было» уже с правки Ven4Tools.
        /// </summary>
        public bool Capture(string tweakId, IReadOnlyList<DebloatRegistryChange> registry, string? service)
        {
            if (registry.Count == 0 && service == null) return true;

            lock (_gate)
            {
                var all = Load(out bool readable);
                if (UndoFileGuard.Broken(_path) is { } bad)
                {
                    AppLogger.Write($"⚠️ {Section}: {UndoFileGuard.BrokenMessage(bad)}");
                    return false;
                }
                // Файл есть, но сейчас не читается: сохранить поверх него значило бы
                // потерять прежние записи отката.
                if (!readable)
                {
                    AppLogger.Write($"⚠️ {Section}: файл отката не читается — «{tweakId}» не применён, чтобы не потерять прежнее состояние.");
                    return false;
                }
                if (all.ContainsKey(tweakId)) return true;

                _owners?.Reconcile(OwnerPrefix, all.Keys.Select(Owner));
                var record = new UndoRecord { AppliedUtc = DateTime.UtcNow };
                foreach (var change in registry)
                {
                    // Оригинал — из общего учёта, если значение уже держит другой раздел
                    // («Панель задач и Пуск»): в реестре сейчас его правка, а не исходное.
                    RegistryRaw ReadNow() => RegistryRaw.From(_system.ReadRaw(change.Path, change.Name));
                    var raw = _owners?.Claim(change.Path, change.Name, Owner(tweakId), ReadNow) ?? ReadNow();
                    // Своя запись — DWORD или отсутствие (как и раньше); значение другого вида
                    // побайтно возвращается по оригиналу из общего учёта.
                    bool isDword = raw.TryGetDword(out int dword);
                    record.Registry.Add(new RegistryState
                    {
                        Path = change.Path, Name = change.Name, Existed = isDword, Value = isDword ? dword : 0,
                    });
                }
                // Одну службу отключают два твика (diag_track и svc_diagtrack): если её уже
                // запомнил другой, берём его «как было» — иначе второй запомнил бы «отключена»
                // и после отката служба так и осталась бы выключенной.
                var prior = service == null ? null : all.Values.SelectMany(r => r.Services)
                    .FirstOrDefault(s => string.Equals(s.Name, service, StringComparison.OrdinalIgnoreCase));
                if (service != null && (prior?.StartMode ?? _system.ReadServiceStartMode(service)) is { } mode)
                    record.Services.Add(new ServiceState { Name = service, StartMode = mode });

                // Службы нет в системе, а значений реестра твик не трогает — возвращать нечего.
                if (record.Registry.Count == 0 && record.Services.Count == 0) return true;

                all[tweakId] = record;
                Save(all);
                return true;
            }
        }

        /// <summary>
        /// Возвращает то, что было до твика. Запись убирается только при полном успехе:
        /// после частичного отката кнопку «Вернуть» можно нажать ещё раз.
        /// </summary>
        public async Task<bool> UndoAsync(string tweakId, CancellationToken ct = default)
        {
            UndoRecord? record;
            lock (_gate)
            {
                var all = Load(out bool readable);
                all.TryGetValue(tweakId, out record);
                // Владельцы «Очистки» без записи отката (файл удалён, сбой) больше ничего не держат.
                if (readable && UndoFileGuard.Broken(_path) == null) _owners?.Reconcile(OwnerPrefix, all.Keys.Select(Owner));
            }
            if (record == null) return false;

            // Файл записей лежит в профиле пользователя, а откат идёт с правами
            // администратора. Из файла берётся только «как было» (значение, режим
            // запуска); ЧТО менять — только из описания самого твика. Иначе правкой
            // файла можно было бы записать любое значение в HKLM или включить любую службу.
            var (knownRegistry, knownServices) = DebloatTweakExecutor.KnownChangesOf(tweakId);
            var registry = record.Registry.Where(state => knownRegistry.Any(change =>
                change.Path.Equals(state.Path, StringComparison.OrdinalIgnoreCase)
                && change.Name.Equals(state.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            var services = record.Services.Where(state =>
                knownServices.Contains(state.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            if (registry.Count != record.Registry.Count || services.Count != record.Services.Count)
                AppLogger.Write($"[Очистка] В записи отката «{tweakId}» есть строки, которых нет в описании твика, — они пропущены.");
            if (registry.Count == 0 && services.Count == 0) return false;

            bool ok = true;
            string owner = Owner(tweakId);
            foreach (var state in registry)
            {
                // Значение ещё держит другой раздел («Панель задач и Пуск»): реестр не
                // трогаем, только снимаем своё владение — оригинал вернёт последний владелец.
                if (_owners?.HeldByOthers(state.Path, state.Name, owner) == true)
                {
                    AppLogger.Write($"[{Section}] «{state.Name}» ещё держит другой раздел — значение оставлено, снято только владение «{tweakId}».");
                    continue;
                }
                ok &= RestoreValue(state, _owners?.Original(state.Path, state.Name));
            }
            foreach (var state in services)
                ok &= await _system.SetServiceStartModeAsync(state.Name, state.StartMode, ct);

            if (ok)
            {
                foreach (var state in registry) _owners?.Release(state.Path, state.Name, owner);
                lock (_gate)
                {
                    var all = Load(out bool readable);
                    if (readable && all.Remove(tweakId)) Save(all);
                }
            }
            return ok;
        }

        /// <summary>
        /// Вернуть одно значение: по оригиналу из общего учёта (побайтно, с видом значения),
        /// а без него — по своей записи (DWORD или отсутствие).
        /// </summary>
        private bool RestoreValue(RegistryState state, RegistryRaw? original)
        {
            if (original != null)
            {
                try
                {
                    return original.ToValue() is { } value
                        ? _system.WriteRaw(state.Path, state.Name, value.Kind, value.Data)
                        : _system.DeleteValue(state.Path, state.Name);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or OverflowException)
                {
                    AppLogger.Write($"[{Section}] Оригинал «{state.Name}» в учёте владения не разобран: {ex.Message} — возвращаю по своей записи.");
                }
            }
            return state.Existed
                ? _system.WriteDword(state.Path, state.Name, state.Value)
                : _system.DeleteValue(state.Path, state.Name);
        }

        private Dictionary<string, UndoRecord> Load() => Load(out _);

        /// <param name="readable">
        /// false — файл есть, но прочитать его сейчас не удалось (занят, нет доступа).
        /// Тогда сохранять поверх нельзя: пропали бы все прежние записи.
        /// </param>
        private Dictionary<string, UndoRecord> Load(out bool readable)
        {
            readable = true;
            try
            {
                if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
                var data = JsonSerializer.Deserialize<Dictionary<string, UndoRecord>>(File.ReadAllText(_path));
                return data == null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, UndoRecord>(
                        data.Where(pair => pair.Value != null), StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex)
            {
                // Испорченный файл уже ничего не вернёт; он откладывается в сторону (.bad),
                // и пока его не разобрали, твики с откатом не применяются (см. Capture).
                var bad = FileHelper.SetAsideBroken(_path);
                AppLogger.Write($"[Очистка] Записи отката испорчены и отложены как {bad ?? "— не отложены"}: {ex.Message}");
                if (bad == null) readable = false;
                return new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Очистка] Записи отката не прочитаны: {ex.Message}");
                readable = false;
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save(Dictionary<string, UndoRecord> all)
        {
            try
            {
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

            public (RegistryValueKind Kind, object Data)? ReadRaw(string path, string name)
            {
                try
                {
                    using var key = Open(path, writable: false);
                    var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return value == null ? null : (key!.GetValueKind(name), value);
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Очистка] Не прочитано значение реестра {name}: {ex.Message}");
                    return null;
                }
            }

            public bool WriteRaw(string path, string name, RegistryValueKind kind, object data)
            {
                try
                {
                    if (!TrySplit(path, out var hive, out string subKey)) return false;
                    using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var key = root.CreateSubKey(subKey, writable: true);
                    key.SetValue(name, data, kind);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Очистка] Не записано значение реестра {name}: {ex.Message}");
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
