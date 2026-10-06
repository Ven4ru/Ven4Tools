using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Журнал применённых твиков «Очистки»: что пользователь когда-то применил.
    ///
    /// Записи отката (<see cref="DebloatUndoService"/>) для этого не годятся: они есть
    /// только у твиков реестра и служб, а удалённые встроенные приложения в них не
    /// попадают. Между тем крупное обновление Windows возвращает и то, и другое.
    /// </summary>
    public sealed class DebloatAppliedJournal
    {
        public static DebloatAppliedJournal Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ven4Tools", "debloat_applied.json"));

        private readonly string _path;
        private readonly object _gate = new();

        public DebloatAppliedJournal(string path) => _path = path;

        public IReadOnlyCollection<string> AppliedTweaks()
        {
            lock (_gate) return Load().Keys.ToList();
        }

        public void MarkApplied(string tweakId)
        {
            lock (_gate)
            {
                var all = Load();
                all[tweakId] = DateTime.UtcNow;
                Save(all);
            }
        }

        /// <summary>Твик возвращён пользователем — следить за ним больше незачем.</summary>
        public void Forget(string tweakId)
        {
            lock (_gate)
            {
                var all = Load();
                if (all.Remove(tweakId)) Save(all);
            }
        }

        private Dictionary<string, DateTime> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
                var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path));
                return data == null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, DateTime>(data, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Очистка] Журнал применённых твиков не прочитан: {ex.Message}");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save(Dictionary<string, DateTime> all)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                FileHelper.WriteAllTextAtomic(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Очистка] Журнал применённых твиков не сохранён: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Проверка твиков после обновления Windows: какие из применённых система вернула
    /// обратно. Крупные обновления заново включают отключённые службы, сбрасывают
    /// значения реестра и ставят обратно удалённые встроенные приложения — молча.
    /// </summary>
    public static class DebloatDriftService
    {
        /// <summary>Служба отключена — режим запуска 4 (см. <see cref="IDebloatSystemState"/>).</summary>
        private const int ServiceDisabled = 4;

        /// <param name="appliedTweaks">Применённые твики: категория и идентификатор.</param>
        /// <param name="installedAppx">
        /// Имена установленных пакетов Appx; null — список получить не удалось, и тогда
        /// удалённые приложения не проверяются (а не объявляются вернувшимися).
        /// </param>
        /// <returns>Идентификаторы твиков, которые сейчас не действуют.</returns>
        public static IReadOnlyList<string> FindDrifted(
            IEnumerable<(string Category, string Id)> appliedTweaks,
            IDebloatSystemState system,
            IReadOnlyCollection<string>? installedAppx)
        {
            var drifted = new List<string>();
            foreach (var (category, id) in appliedTweaks)
            {
                if (IsDrifted(category, id, system, installedAppx)) drifted.Add(id);
            }
            return drifted;
        }

        private static bool IsDrifted(
            string category, string id, IDebloatSystemState system, IReadOnlyCollection<string>? installedAppx)
        {
            if (category == "app")
            {
                // Удаление идёт по вхождению имени (см. RemoveAppxAsync) — так же и ищем.
                return installedAppx != null
                    && installedAppx.Any(name => name.Contains(id, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var change in DebloatTweakExecutor.RegistryChangesOf(category, id))
            {
                var (exists, value) = system.ReadDword(change.Path, change.Name);
                if (!exists || value != change.Value) return true;
            }

            // Службы нет в системе — возвращать нечего, это не откат твика.
            string? service = DebloatTweakExecutor.ServiceOf(category, id);
            return service != null
                && system.ReadServiceStartMode(service) is { } mode
                && mode != ServiceDisabled;
        }

        /// <summary>
        /// Имена пакетов Appx текущего пользователя; null, если PowerShell не отработал.
        /// </summary>
        public static async Task<IReadOnlyCollection<string>?> ListInstalledAppxAsync(CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo(TrustedExecutablePaths.PowerShellExe,
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-AppxPackage | ForEach-Object { $_.Name }\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;

                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    await p.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    AppLogger.Write("[Очистка] Список пакетов Appx не получен: тайм-аут или отмена");
                    return null;
                }

                await Task.WhenAll(outTask, errTask);
                if (p.ExitCode != 0) return null;

                return outTask.Result
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Очистка] Список пакетов Appx не получен: {ex.Message}");
                return null;
            }
        }
    }
}
