using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Службы и папки для сброса Центра обновления; вынесено интерфейсом ради тестов.</summary>
    public interface IWuResetEnvironment
    {
        bool ServiceExists(string service);
        bool IsServiceRunning(string service);
        bool IsServiceDisabled(string service);
        /// <summary>Запущенные службы, зависящие от этой (их остановит и Stop).</summary>
        IReadOnlyList<string> RunningDependents(string service);
        /// <summary>Останавливает службу вместе с зависимыми; true — остановлена.</summary>
        bool StopService(string service, TimeSpan timeout);
        bool StartService(string service, TimeSpan timeout);
        bool DirectoryExists(string path);
        /// <summary>Переименование папки; при неудаче — исключение.</summary>
        void MoveDirectory(string from, string to);
        DateTime Now { get; }
        void Sleep(TimeSpan delay);
    }

    /// <summary>Итог сброса Центра обновления.</summary>
    public sealed class WuResetResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        /// <summary>Куда переименованы папки (оригинал → копия), пусто при неудаче.</summary>
        public IReadOnlyList<(string From, string To)> Renamed { get; init; } = Array.Empty<(string, string)>();
        /// <summary>Службы, которые не удалось запустить обратно.</summary>
        public IReadOnlyList<string> NotStarted { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// «Сбросить Центр обновления» — классический ручной рецепт Microsoft: остановить
    /// wuauserv, bits, cryptsvc, msiserver; переименовать SoftwareDistribution и
    /// catroot2 в *.old-&lt;дата&gt;; запустить службы. Windows создаст папки заново.
    /// <para>
    /// Ничего не удаляется: старые папки остаются рядом и при необходимости
    /// возвращаются переименованием. Если хоть одна обязательная служба не
    /// останавливается — ничего не переименовывается; если не удалось переименовать
    /// вторую папку — первая возвращается обратно. Службы в любом исходе запускаются.
    /// </para>
    /// <para>
    /// Дополнительно (без отказа при неудаче) останавливаются UsoSvc и DoSvc: первая
    /// перезапускает wuauserv по расписанию, вторая держит свой кэш внутри
    /// SoftwareDistribution — обе мешают переименованию.
    /// </para>
    /// </summary>
    public sealed class WindowsUpdateResetService
    {
        public static readonly string[] RequiredServices = { "wuauserv", "bits", "cryptsvc", "msiserver" };
        private static readonly string[] HelperServices = { "usosvc", "dosvc" };

        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

        private readonly IWuResetEnvironment _env;
        private readonly string _softwareDistribution;
        private readonly string _catroot2;

        public WindowsUpdateResetService() : this(new RealEnvironment(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "catroot2"))
        {
        }

        public WindowsUpdateResetService(IWuResetEnvironment env, string softwareDistribution, string catroot2)
        {
            _env = env;
            _softwareDistribution = softwareDistribution;
            _catroot2 = catroot2;
        }

        /// <summary>
        /// Имя копии: «SoftwareDistribution.old-20261021-153000»; если такая уже есть —
        /// с хвостом «-2», «-3»… Чистая функция.
        /// </summary>
        public static string BuildBackupPath(string original, DateTime now, Func<string, bool> exists)
        {
            string trimmed = original.TrimEnd('\\', '/');
            string basePath = $"{trimmed}.old-{now:yyyyMMdd-HHmmss}";
            if (!exists(basePath)) return basePath;
            for (int i = 2; i < 100; i++)
            {
                string candidate = $"{basePath}-{i}";
                if (!exists(candidate)) return candidate;
            }
            return $"{basePath}-{Guid.NewGuid():N}";
        }

        public WuResetResult Reset()
        {
            // Что было запущено до нас — то и запускается обратно, плюс зависимые службы.
            var all = RequiredServices.Concat(HelperServices).Where(_env.ServiceExists).ToList();
            var wasRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in all)
            {
                if (_env.IsServiceRunning(s)) wasRunning.Add(s);
                foreach (var dep in _env.RunningDependents(s)) wasRunning.Add(dep);
            }

            var stopped = new List<string>();
            foreach (var s in RequiredServices.Where(_env.ServiceExists))
            {
                if (!_env.IsServiceRunning(s)) continue;
                AppLogger.Write($"[Ремонт] Остановка службы {s}");
                if (!_env.StopService(s, StopTimeout))
                {
                    AppLogger.Write($"[Ремонт] Служба {s} не остановилась — сброс прерван, папки не тронуты");
                    var notStarted = StartBack(wasRunning);
                    return new WuResetResult
                    {
                        Message = $"Служба «{s}» не останавливается, поэтому сброс не выполнен — папки не тронуты. " +
                                  "Обычно помогает перезагрузка компьютера, после неё попробуйте ещё раз.",
                        NotStarted = notStarted
                    };
                }
                stopped.Add(s);
            }
            foreach (var s in HelperServices.Where(_env.ServiceExists))
            {
                if (_env.IsServiceRunning(s) && !_env.StopService(s, StopTimeout))
                    AppLogger.Write($"[Ремонт] Вспомогательная служба {s} не остановилась — продолжаем");
            }

            var renamed = new List<(string From, string To)>();
            string? failure = null;
            foreach (var folder in new[] { _softwareDistribution, _catroot2 })
            {
                if (!_env.DirectoryExists(folder)) continue;
                string target = BuildBackupPath(folder, _env.Now, _env.DirectoryExists);
                if (TryMove(folder, target, out string error))
                {
                    renamed.Add((folder, target));
                    AppLogger.Write($"[Ремонт] {folder} → {target}");
                }
                else
                {
                    failure = $"Не удалось переименовать {folder}: {error}";
                    break;
                }
            }

            if (failure != null)
            {
                AppLogger.Write($"[Ремонт] {failure} — возвращаем уже переименованное");
                var rollbackErrors = new List<string>();
                foreach (var (from, to) in Enumerable.Reverse(renamed))
                {
                    if (!TryMove(to, from, out string error))
                        rollbackErrors.Add($"{to} → {from}: {error}");
                }
                var notStarted = StartBack(wasRunning);
                string message = $"{failure}. Сброс не выполнен.";
                if (renamed.Count > 0)
                    message += rollbackErrors.Count == 0
                        ? " Уже переименованные папки возвращены на место."
                        : " Вернуть на место не удалось: " + string.Join("; ", rollbackErrors) +
                          ". Переименуйте их обратно вручную после перезагрузки.";
                return new WuResetResult { Message = message, NotStarted = notStarted };
            }

            var notStartedOk = StartBack(wasRunning);
            return new WuResetResult
            {
                Success = true,
                Renamed = renamed,
                NotStarted = notStartedOk,
                Message = renamed.Count == 0
                    ? "Папок Центра обновления не нашлось — переименовывать было нечего, службы перезапущены."
                    : "Центр обновления сброшен. Старые папки сохранены рядом: " +
                      string.Join(", ", renamed.Select(r => Path.GetFileName(r.To))) + "."
            };
        }

        /// <summary>
        /// Переименование с повтором: wuauserv могут запустить снова (UsoSvc, задания
        /// планировщика) между остановкой и переименованием — тогда она останавливается
        /// ещё раз и попытка повторяется.
        /// </summary>
        private bool TryMove(string from, string to, out string error)
        {
            error = "";
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    _env.MoveDirectory(from, to);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    AppLogger.Write($"[Ремонт] Переименование {from} (попытка {attempt}): {ex.Message}");
                    foreach (var s in RequiredServices.Concat(HelperServices).Where(_env.ServiceExists))
                        if (_env.IsServiceRunning(s)) _env.StopService(s, StopTimeout);
                    _env.Sleep(TimeSpan.FromSeconds(2));
                }
            }
            return false;
        }

        /// <summary>
        /// Запускает службы, которые работали до сброса, плюс cryptsvc (она в Windows
        /// всегда запущена, без неё не проверяются подписи). Отключённые не трогаются.
        /// </summary>
        private List<string> StartBack(HashSet<string> wasRunning)
        {
            var toStart = new HashSet<string>(wasRunning, StringComparer.OrdinalIgnoreCase) { "cryptsvc" };
            var order = Enumerable.Reverse(RequiredServices).Concat(HelperServices)
                .Concat(toStart.Where(s => !RequiredServices.Contains(s, StringComparer.OrdinalIgnoreCase)
                                        && !HelperServices.Contains(s, StringComparer.OrdinalIgnoreCase)))
                .Where(toStart.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var notStarted = new List<string>();
            foreach (var s in order)
            {
                if (!_env.ServiceExists(s) || _env.IsServiceRunning(s)) continue;
                if (_env.IsServiceDisabled(s)) continue;
                if (!_env.StartService(s, StartTimeout))
                {
                    AppLogger.Write($"[Ремонт] Служба {s} не запустилась");
                    notStarted.Add(s);
                }
            }
            return notStarted;
        }

        // ── Настоящая система ────────────────────────────────────────────────────

        private sealed class RealEnvironment : IWuResetEnvironment
        {
            public DateTime Now => DateTime.Now;
            public void Sleep(TimeSpan delay) => Thread.Sleep(delay);

            public bool ServiceExists(string service)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    _ = sc.Status;
                    return true;
                }
                catch (Exception) { return false; }
            }

            public bool IsServiceRunning(string service)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    return sc.Status != ServiceControllerStatus.Stopped;
                }
                catch (Exception) { return false; }
            }

            public bool IsServiceDisabled(string service)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    return sc.StartType == ServiceStartMode.Disabled;
                }
                catch (Exception) { return false; }
            }

            public IReadOnlyList<string> RunningDependents(string service)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    var result = new List<string>();
                    foreach (var dep in sc.DependentServices)
                    {
                        using (dep)
                            if (dep.Status != ServiceControllerStatus.Stopped) result.Add(dep.ServiceName);
                    }
                    return result;
                }
                catch (Exception) { return Array.Empty<string>(); }
            }

            public bool StopService(string service, TimeSpan timeout)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    if (sc.Status == ServiceControllerStatus.Stopped) return true;
                    if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop(); // останавливает и зависимые
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    sc.Refresh();
                    return sc.Status == ServiceControllerStatus.Stopped;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Ремонт] Остановка {service}: {ex.Message}");
                    return false;
                }
            }

            public bool StartService(string service, TimeSpan timeout)
            {
                try
                {
                    using var sc = new ServiceController(service);
                    if (sc.Status == ServiceControllerStatus.Running) return true;
                    if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    sc.Refresh();
                    return sc.Status == ServiceControllerStatus.Running;
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[Ремонт] Запуск {service}: {ex.Message}");
                    return false;
                }
            }

            public bool DirectoryExists(string path) => Directory.Exists(path);

            public void MoveDirectory(string from, string to) => Directory.Move(from, to);
        }
    }
}
