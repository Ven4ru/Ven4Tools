using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Состояние компонента Windows по данным DISM.</summary>
    public enum WindowsFeatureState
    {
        Unknown,
        Enabled,
        Disabled,
        /// <summary>Отключён, и файлов компонента на диске нет — их придётся скачать или взять с носителя.</summary>
        DisabledPayloadRemoved,
        EnablePending,
        DisablePending
    }

    /// <summary>Итог операции DISM.</summary>
    public sealed class DismOperationResult
    {
        public bool Success { get; init; }
        public bool RebootRequired { get; init; }
        public bool Cancelled { get; init; }
        public int ErrorCode { get; init; }
        /// <summary>Понятное объяснение по-русски (пусто при успехе).</summary>
        public string Message { get; init; } = "";
        /// <summary>Ошибка из тех, где поможет установочная флешка/ISO.</summary>
        public bool SuggestSourceMedia { get; init; }

        public static DismOperationResult From(ServicingProcessResult run)
        {
            if (!string.IsNullOrEmpty(run.StartError))
                return new DismOperationResult { ErrorCode = -1, Message = $"Не удалось запустить DISM: {run.StartError}" };
            if (run.Cancelled)
                return new DismOperationResult { Cancelled = true, Message = "Операция отменена." };

            int code = DismErrorMapper.ExtractErrorCode(run.ExitCode, run.Output);
            if (DismErrorMapper.IsSuccess(run.ExitCode) && DismErrorMapper.IsSuccess(code))
                return new DismOperationResult
                {
                    Success = true,
                    RebootRequired = run.ExitCode == DismErrorMapper.RebootRequired || code == DismErrorMapper.RebootRequired
                };
            return new DismOperationResult
            {
                ErrorCode = code,
                Message = DismErrorMapper.Describe(code),
                SuggestSourceMedia = DismErrorMapper.SuggestsSourceMedia(code)
            };
        }
    }

    /// <summary>
    /// .NET Framework 3.5 (компонент NetFx3) — нужен 1С, старым играм и программам.
    /// Всё через штатный dism.exe: состояние (/Get-FeatureInfo), включение через Центр
    /// обновления или с установочного носителя (/Source + /LimitAccess), выключение.
    /// Ключ /English — чтобы вывод разбирался одинаково на любом языке системы.
    /// </summary>
    public static class NetFx3Service
    {
        public const string FeatureName = "NetFx3";

        private static readonly Regex StateLine = new(
            @"^\s*State\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Разбор строки «State : …» из вывода DISM /Get-FeatureInfo /English.</summary>
        public static WindowsFeatureState ParseState(string output)
        {
            var match = StateLine.Match(output ?? "");
            if (!match.Success) return WindowsFeatureState.Unknown;
            return match.Groups[1].Value.Trim().ToLowerInvariant() switch
            {
                "enabled" => WindowsFeatureState.Enabled,
                "disabled" => WindowsFeatureState.Disabled,
                "disabled with payload removed" => WindowsFeatureState.DisabledPayloadRemoved,
                "enable pending" => WindowsFeatureState.EnablePending,
                "disable pending" => WindowsFeatureState.DisablePending,
                _ => WindowsFeatureState.Unknown
            };
        }

        public static async Task<(WindowsFeatureState State, string Error)> GetStateAsync(CancellationToken ct)
        {
            var run = await ServicingProcessRunner.RunAsync(ServicingProcessRunner.DismExe,
                new[] { "/Online", "/English", "/Get-FeatureInfo", "/FeatureName:" + FeatureName }, null, ct);
            if (run.Cancelled) return (WindowsFeatureState.Unknown, "Проверка отменена.");
            var state = ParseState(run.Output);
            if (state != WindowsFeatureState.Unknown) return (state, "");
            var result = DismOperationResult.From(run);
            return (WindowsFeatureState.Unknown, result.Success ? "DISM не сообщил состояние компонента." : result.Message);
        }

        /// <summary>
        /// Включает .NET 3.5. source = null — файлы берутся из Центра обновления;
        /// иначе путь к папке sources\sxs установочного носителя, и Центр обновления не
        /// используется (/LimitAccess).
        /// </summary>
        public static async Task<DismOperationResult> EnableAsync(string? source, Action<double>? onPercent, CancellationToken ct)
        {
            var args = new List<string> { "/Online", "/English", "/Enable-Feature", "/FeatureName:" + FeatureName, "/All", "/NoRestart" };
            if (!string.IsNullOrEmpty(source))
            {
                args.Add("/Source:" + source);
                args.Add("/LimitAccess");
            }
            var run = await ServicingProcessRunner.RunAsync(ServicingProcessRunner.DismExe, args, onPercent, ct);
            LogRun("Включение .NET Framework 3.5" + (source != null ? $" с носителя {source}" : " через Центр обновления"), run);
            return DismOperationResult.From(run);
        }

        public static async Task<DismOperationResult> DisableAsync(Action<double>? onPercent, CancellationToken ct)
        {
            var run = await ServicingProcessRunner.RunAsync(ServicingProcessRunner.DismExe,
                new[] { "/Online", "/English", "/Disable-Feature", "/FeatureName:" + FeatureName, "/NoRestart" }, onPercent, ct);
            LogRun("Выключение .NET Framework 3.5", run);
            return DismOperationResult.From(run);
        }

        /// <summary>
        /// По выбранной пользователем папке находит sources\sxs: подойдёт корень флешки
        /// или смонтированного ISO, папка sources или сама sxs. null — такой папки нет.
        /// </summary>
        public static string? ResolveSxsFolder(string chosen, Func<string, bool> directoryExists)
        {
            if (string.IsNullOrWhiteSpace(chosen)) return null;
            string path = chosen.Trim().TrimEnd('\\', '/');
            if (path.Length == 2 && path[1] == ':') path += "\\"; // «E:» → «E:\»

            var candidates = new[]
            {
                Path.Combine(path, "sources", "sxs"),
                Path.Combine(path, "sxs"),
                path
            };
            foreach (var candidate in candidates)
            {
                bool isSxs = string.Equals(Path.GetFileName(candidate.TrimEnd('\\')), "sxs", StringComparison.OrdinalIgnoreCase);
                if (isSxs && directoryExists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Штатно монтирует ISO (Mount-DiskImage) и возвращает корень диска («E:\»).
        /// Путь передаётся через переменную окружения, а не вклейкой в текст команды, —
        /// кавычки и спецсимволы в имени файла ничего не сломают.
        /// </summary>
        public static async Task<(string? Root, string Error)> MountIsoAsync(string isoPath, CancellationToken ct)
        {
            const string script =
                "$ErrorActionPreference='Stop';" +
                "$img = Mount-DiskImage -ImagePath $env:V4T_ISO_PATH -PassThru;" +
                "$vol = $img | Get-Volume;" +
                "[Console]::Out.Write($vol.DriveLetter)";
            var run = await ServicingProcessRunner.RunAsync(TrustedExecutablePaths.PowerShellExe,
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
                null, ct, new Dictionary<string, string> { ["V4T_ISO_PATH"] = isoPath });
            string letter = run.Output.Trim();
            if (run.ExitCode == 0 && letter.Length >= 1 && char.IsLetter(letter[0]))
                return ($"{char.ToUpperInvariant(letter[0])}:\\", "");
            AppLogger.Write($"[Компоненты] Не удалось смонтировать ISO: код {run.ExitCode}, {run.Output.Trim()}");
            return (null, "Не удалось подключить образ ISO. Проверьте, что файл не повреждён и не открыт в другой программе.");
        }

        public static async Task DismountIsoAsync(string isoPath)
        {
            const string script = "Dismount-DiskImage -ImagePath $env:V4T_ISO_PATH | Out-Null";
            var run = await ServicingProcessRunner.RunAsync(TrustedExecutablePaths.PowerShellExe,
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
                null, CancellationToken.None, new Dictionary<string, string> { ["V4T_ISO_PATH"] = isoPath });
            if (run.ExitCode != 0)
                AppLogger.Write($"[Компоненты] Образ ISO не отключён (код {run.ExitCode}) — его можно извлечь в Проводнике.");
        }

        private static void LogRun(string what, ServicingProcessResult run)
        {
            int code = DismErrorMapper.ExtractErrorCode(run.ExitCode, run.Output);
            AppLogger.Write(run.Cancelled
                ? $"[Компоненты] {what}: отменено пользователем"
                : $"[Компоненты] {what}: код {run.ExitCode}" + (code != run.ExitCode ? $", ошибка 0x{code:X8}" : ""));
            // Хвост вывода DISM — для разбора жалоб; целиком он длинный (полосы прогресса).
            var tail = run.Output.Split('\r', '\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('[')).TakeLast(6);
            foreach (var line in tail) AppLogger.Write($"[Компоненты]   {line}");
        }
    }
}
