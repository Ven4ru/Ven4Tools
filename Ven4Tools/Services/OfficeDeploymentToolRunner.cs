using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Shared;

namespace Ven4Tools.Services
{
    public sealed class OdtPrepareResult
    {
        public bool Success { get; private init; }
        public string SetupExePath { get; private init; } = "";
        public string Error { get; private init; } = "";

        public static OdtPrepareResult Ok(string setupExePath) => new() { Success = true, SetupExePath = setupExePath };
        public static OdtPrepareResult Failed(string error) => new() { Success = false, Error = error };
    }

    /// <summary>
    /// Подготовка Office Deployment Tool и запуск удаления/установки через его
    /// универсальный контракт "/configure config.xml". ODT получаем через
    /// <c>winget download --id Microsoft.OfficeDeploymentTool</c>, а не по
    /// захардкоженной ссылке (см. "Отступление от буквы спеки" в шапке плана
    /// docs/superpowers/plans/2026-09-14-office-uninstall-and-version-change-plan.md) —
    /// актуальность ссылки становится проблемой манифеста winget, той же модели
    /// доверия, что уже используется для всего каталога.
    /// </summary>
    public sealed class OfficeDeploymentToolRunner
    {
        public static string BuildRemoveAllConfigurationXml() =>
            "<Configuration><Remove All=\"TRUE\"/><Display Level=\"None\" AcceptEULA=\"TRUE\"/></Configuration>";

        /// <summary>Адресное удаление конкретных ProductReleaseIds (не всего C2R разом).</summary>
        public static string BuildRemoveProductsConfigurationXml(IReadOnlyList<string> productIds)
        {
            if (productIds == null || productIds.Count == 0)
                throw new ArgumentException("Нужен хотя бы один ProductReleaseId.", nameof(productIds));

            string products = string.Concat(
                productIds.Select(id => $"<Product ID=\"{SecurityElement.Escape(id)}\"/>"));
            return $"<Configuration><Remove>{products}</Remove><Display Level=\"None\" AcceptEULA=\"TRUE\"/></Configuration>";
        }

        /// <summary>
        /// Скачивает ODT через winget, проверяет Authenticode-подпись Microsoft,
        /// распаковывает через "/extract:&lt;dir&gt; /quiet" и возвращает путь к setup.exe.
        /// </summary>
        public async Task<OdtPrepareResult> PrepareAsync(CancellationToken ct)
        {
            string workDir = Path.Combine(Path.GetTempPath(), $"Ven4Tools-ODT-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(workDir);

                var (exitCode, output) = await WingetRunner.RunAsync(
                    WingetArgs.Modify("download", "--id", "Microsoft.OfficeDeploymentTool", "--exact", "-d", workDir),
                    TimeSpan.FromMinutes(3), ct);

                if (exitCode != 0)
                    return OdtPrepareResult.Failed($"winget download завершился с кодом {exitCode}: {WingetRunner.StripAnsi(output)}");

                string? bootstrapperPath = Directory.GetFiles(workDir, "*.exe").FirstOrDefault();
                if (bootstrapperPath == null)
                    return OdtPrepareResult.Failed("winget download не создал исполняемый файл ODT");

                if (!AuthenticodeVerifier.IsSignedByMicrosoft(bootstrapperPath, out string signatureError))
                    return OdtPrepareResult.Failed($"Подпись ODT не подтверждена: {signatureError}");

                string extractDir = Path.Combine(workDir, "extracted");
                Directory.CreateDirectory(extractDir);

                var extractPsi = new ProcessStartInfo
                {
                    FileName = bootstrapperPath,
                    Arguments = $"/extract:\"{extractDir}\" /quiet",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var extractProcess = Process.Start(extractPsi);
                if (extractProcess == null)
                    return OdtPrepareResult.Failed("Не удалось запустить самораспаковку ODT");

                await extractProcess.WaitForExitAsync(ct);
                if (extractProcess.ExitCode != 0)
                    return OdtPrepareResult.Failed($"Самораспаковка ODT завершилась с кодом {extractProcess.ExitCode}");

                string setupExePath = Path.Combine(extractDir, "setup.exe");
                if (!File.Exists(setupExePath))
                    return OdtPrepareResult.Failed("setup.exe не найден после распаковки ODT");

                return OdtPrepareResult.Ok(setupExePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return OdtPrepareResult.Failed($"Подготовка ODT: {ex.Message}");
            }
        }

        /// <summary>
        /// Запускает "&lt;exePath&gt; /configure &lt;configXmlPath&gt;" с повышением прав —
        /// работает одинаково для свежераспакованного setup.exe (из PrepareAsync) и для
        /// уже установленного OfficeClickToRun.exe (запасной путь, когда ODT недоступен:
        /// это тот же C2R-клиент, тот же контракт "/configure"). Возвращает код возврата
        /// процесса; -1, если процесс не удалось запустить.
        /// </summary>
        public async Task<int> RunConfigureAsync(string exePath, string configXmlPath, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"/configure \"{configXmlPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return -1;

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
    }
}
