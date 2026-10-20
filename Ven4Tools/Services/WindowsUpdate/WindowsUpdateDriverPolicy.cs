using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Доступ к значению политики в реестре; вынесен интерфейсом ради тестов.</summary>
    public interface IDriverPolicyStore
    {
        /// <summary>Exists = false — значения нет. Readable = false — прочитать не удалось.</summary>
        (bool Readable, bool Exists, int Value) Read();
        bool Write(int value);
        bool Delete();
    }

    /// <summary>Прежнее состояние значения — то, что было до первого включения запрета.</summary>
    public sealed class DriverPolicyPrior
    {
        public bool Existed { get; set; }
        public int Value { get; set; }
    }

    /// <summary>Что сделать с реестром при выключении запрета.</summary>
    public enum DriverPolicyRestoreAction { Delete, WritePrior }

    /// <summary>
    /// Переключатель «Не получать драйверы через Windows Update» — политика
    /// HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate, значение
    /// ExcludeWUDriversInQualityUpdate = 1 (DWORD).
    /// <para>
    /// Политика лишь просит Windows Update не включать драйверы в ежемесячные
    /// обновления. Она снижает вероятность, что поверх нового драйвера видеокарты
    /// вернётся старый, но не гарантирует этого: драйверы ставит и мастер устройств при
    /// подключении нового оборудования, а в организации значение может перезаписать
    /// администратор домена. Поэтому в интерфейсе она так и описана.
    /// </para>
    /// <para>
    /// Перед первым включением запоминается прежнее состояние значения (в профиле
    /// пользователя, wu_driver_policy.json). Выключение возвращает его: если значения
    /// не было — удаляет, если было другое — записывает прежнее. ЧТО менять (ключ и
    /// имя значения) задано только здесь, из файла берётся лишь прежнее число.
    /// </para>
    /// </summary>
    public sealed class WindowsUpdateDriverPolicy
    {
        public const string KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        public const string ValueName = "ExcludeWUDriversInQualityUpdate";

        public static WindowsUpdateDriverPolicy Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "wu_driver_policy.json"),
            new RegistryDriverPolicyStore());

        private readonly string _priorPath;
        private readonly IDriverPolicyStore _store;
        private readonly object _gate = new();

        public WindowsUpdateDriverPolicy(string priorPath, IDriverPolicyStore store)
        {
            _priorPath = priorPath;
            _store = store;
        }

        /// <summary>true — запрет действует; false — нет; null — реестр не прочитан.</summary>
        public bool? IsBlocked()
        {
            var (readable, exists, value) = _store.Read();
            if (!readable) return null;
            return exists && value == 1;
        }

        /// <summary>
        /// Включает запрет. Прежнее состояние запоминается только при первом включении:
        /// повторное включение его не перезаписывает, иначе «прежним» стала бы единица.
        /// </summary>
        public bool Block()
        {
            lock (_gate)
            {
                var (readable, exists, value) = _store.Read();
                if (!readable) return false;
                if (exists && value == 1) return true;

                if (LoadPrior() == null && !SavePrior(new DriverPolicyPrior { Existed = exists, Value = value }))
                {
                    // Не записав «как было», включать нельзя: выключение потом не смогло бы
                    // вернуть прежнее значение, а лишь удалило бы его.
                    AppLogger.Write("[WindowsUpdate] Прежнее значение политики драйверов не сохранено — запрет не включён");
                    return false;
                }
                return _store.Write(1);
            }
        }

        /// <summary>Выключает запрет: возвращает прежнее состояние или удаляет значение.</summary>
        public bool Unblock()
        {
            lock (_gate)
            {
                var prior = LoadPrior();
                var (action, value) = PlanRestore(prior);
                bool ok = action == DriverPolicyRestoreAction.WritePrior
                    ? _store.Write(value)
                    : _store.Delete();
                if (ok) DeletePrior();
                return ok;
            }
        }

        /// <summary>
        /// Чистая логика выключения. Прежнее значение, которое само было единицей (то есть
        /// запрет включили до нас), возвращать бессмысленно: пользователь явно попросил
        /// драйверы через Windows Update получать — значение удаляется.
        /// </summary>
        public static (DriverPolicyRestoreAction Action, int Value) PlanRestore(DriverPolicyPrior? prior)
        {
            if (prior is { Existed: true } && prior.Value != 1)
                return (DriverPolicyRestoreAction.WritePrior, prior.Value);
            return (DriverPolicyRestoreAction.Delete, 0);
        }

        private DriverPolicyPrior? LoadPrior()
        {
            try
            {
                if (!File.Exists(_priorPath)) return null;
                return JsonSerializer.Deserialize<DriverPolicyPrior>(File.ReadAllText(_priorPath));
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdate] Прежнее значение политики драйверов не прочитано: {ex.Message}");
                return null;
            }
        }

        private bool SavePrior(DriverPolicyPrior prior)
        {
            try
            {
                FileHelper.WriteAllTextAtomic(_priorPath, JsonSerializer.Serialize(prior));
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdate] Прежнее значение политики драйверов не сохранено: {ex.Message}");
                return false;
            }
        }

        private void DeletePrior()
        {
            try { if (File.Exists(_priorPath)) File.Delete(_priorPath); }
            catch (Exception ex) { AppLogger.Write($"[WindowsUpdate] Запись о прежнем значении политики не удалена: {ex.Message}"); }
        }

        // ── Настоящий реестр ─────────────────────────────────────────────────────

        private sealed class RegistryDriverPolicyStore : IDriverPolicyStore
        {
            public (bool Readable, bool Exists, int Value) Read()
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
                    return key?.GetValue(ValueName) is int value ? (true, true, value) : (true, false, 0);
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdate] Политика драйверов не прочитана: {ex.Message}");
                    return (false, false, 0);
                }
            }

            public bool Write(int value)
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
                    key.SetValue(ValueName, value, RegistryValueKind.DWord);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdate] Политика драйверов не записана: {ex.Message}");
                    return false;
                }
            }

            public bool Delete()
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
                    key?.DeleteValue(ValueName, throwOnMissingValue: false);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdate] Политика драйверов не удалена: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
