using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Shared;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Результат подготовки ODT. На успехе владеет двумя ресурсами до явного
    /// <see cref="Dispose"/>: рабочей папкой (<see cref="WorkDir"/>, распакованный
    /// setup.exe и — после Task 3 — Configuration.xml) и открытым на чтение
    /// хендлом самого setup.exe (см. комментарий в <see cref="OfficeDeploymentToolRunner.PrepareAsync"/>
    /// про TOCTOU). Вызывающий код обязан вызвать Dispose() ПОСЛЕ того, как
    /// элевированный процесс ЗАВЕРШИЛСЯ, а не сразу после Process.Start: для
    /// хендла setup.exe хватило бы и возврата из Process.Start (так сделано с
    /// installerHandle в OfficeViewModel.Install.cs), но этот Dispose ещё и
    /// удаляет <see cref="WorkDir"/> — снести папку из-под работающего setup.exe
    /// нельзя. См. RunConfigureFlowAsync в OfficeViewModel.Management.cs: там
    /// Dispose стоит в finally после await RunConfigureAsync, который дожидается
    /// выхода процесса.
    /// </summary>
    public sealed class OdtPrepareResult : IDisposable
    {
        public bool Success { get; }
        public string SetupExePath { get; }
        public string Error { get; }

        /// <summary>Рабочая папка ODT (бутстраппер + extracted/). Пусто, если Success == false
        /// (в этом случае папка уже удалена синхронно в FailedAndCleanup).</summary>
        public string WorkDir { get; }

        private readonly FileStream? _setupExeHandle;
        private bool _disposed;

        private OdtPrepareResult(bool success, string setupExePath, string error, string workDir, FileStream? setupExeHandle)
        {
            Success = success;
            SetupExePath = setupExePath;
            Error = error;
            WorkDir = workDir;
            _setupExeHandle = setupExeHandle;
        }

        public static OdtPrepareResult Ok(string setupExePath, string workDir, FileStream setupExeHandle) =>
            new(true, setupExePath, "", workDir, setupExeHandle);

        public static OdtPrepareResult Failed(string error) => new(false, "", error, "", null);

        /// <summary>
        /// Закрывает хендл setup.exe и рекурсивно удаляет WorkDir (включая
        /// Configuration.xml, если вызывающий код записал его туда же — см. Task 3).
        /// Идемпотентен; удаление — best effort, чтобы сбой очистки не маскировал
        /// результат самой операции /configure.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _setupExeHandle?.Dispose();

            if (!string.IsNullOrEmpty(WorkDir))
            {
                try { Directory.Delete(WorkDir, recursive: true); }
                catch { /* очистка — best effort, не должна маскировать результат /configure */ }
            }
        }
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

        /// <summary>
        /// Адресное удаление конкретных ProductReleaseIds (не всего C2R разом) — ODT
        /// это поддерживает, но ни один отгружаемый код сейчас сюда не обращается: и
        /// удаление (RunUninstallAsync), и замена (RunReplaceAsync) в OfficeViewModel.
        /// Management.cs используют только <see cref="BuildRemoveAllConfigurationXml"/>,
        /// как и требуют обе спеки. Метод оставлен как готовая альтернатива на будущее
        /// (когда/если адресное удаление понадобится), а не как реально задействованная
        /// возможность — сейчас его вызывают только тесты.
        /// </summary>
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
            // Не голый %TEMP%: и бутстраппер (самораспаковка — дочерний процесс
            // elevated-клиента), и setup.exe (/configure, runas) запускаются с правами
            // администратора, а соседняя DLL в каталоге запуска грузится ими по порядку
            // поиска — см. InstallerTempDirectory. В защищённом каталоге заодно нельзя
            // подменить ни бутстраппер между проверкой подписи и запуском, ни
            // Configuration.xml между записью и открытием хендла.
            string workDir;
            try
            {
                workDir = Helpers.InstallerTempDirectory.NewFilePath($"Ven4Tools-ODT-{Guid.NewGuid():N}");
            }
            catch (Exception ex)
            {
                return OdtPrepareResult.Failed($"Подготовка ODT: {ex.Message}");
            }

            try
            {
                Directory.CreateDirectory(workDir);

                var (exitCode, output) = await WingetRunner.RunAsync(
                    WingetArgs.Modify("download", "--id", "Microsoft.OfficeDeploymentTool", "--exact", "-d", workDir),
                    TimeSpan.FromMinutes(3), ct);

                if (exitCode != 0)
                    return FailedAndCleanup(workDir, $"winget download завершился с кодом {exitCode}: {WingetRunner.StripAnsi(output)}");

                string? bootstrapperPath = Directory.GetFiles(workDir, "*.exe").FirstOrDefault();
                if (bootstrapperPath == null)
                    return FailedAndCleanup(workDir, "winget download не создал исполняемый файл ODT");

                if (!AuthenticodeVerifier.IsSignedByMicrosoft(bootstrapperPath, out string signatureError))
                    return FailedAndCleanup(workDir, $"Подпись ODT не подтверждена: {signatureError}");

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
                    return FailedAndCleanup(workDir, "Не удалось запустить самораспаковку ODT");

                await extractProcess.WaitForExitAsync(ct);
                if (extractProcess.ExitCode != 0)
                    return FailedAndCleanup(workDir, $"Самораспаковка ODT завершилась с кодом {extractProcess.ExitCode}");

                string setupExePath = Path.Combine(extractDir, "setup.exe");
                if (!File.Exists(setupExePath))
                    return FailedAndCleanup(workDir, "setup.exe не найден после распаковки ODT");

                // Второй, независимый рубеж проверки: setupExePath — это тот самый файл,
                // который Task 3 запускает с Verb="runas" (пересекает границу привилегий),
                // а сам повышенный запуск происходит позже, после UAC-запроса, на котором
                // пользователь может задержаться секунды. Между распаковкой и повышенным
                // запуском файл лежит в пользовательской %TEMP%-папке, где его теоретически
                // может подменить другой процесс того же пользователя со средней
                // целостностью (TOCTOU) — как и Configuration.xml, который Task 3 пишет
                // рядом (ODT поддерживает <Add SourcePath="…">, так что подмена XML тоже
                // превращает элевированный setup.exe в установщик произвольного контента).
                //
                // Решение — то же самое, что уже применено в OfficeViewModel.Install.cs
                // для установщика Office (installerHandle): открываем файл на чтение с
                // FileShare.Read ДО проверки подписи и держим хендл открытым до тех пор,
                // пока элевированный процесс уже не запущен. AuthenticodeVerifier проверяет
                // подпись по пути (hFile=IntPtr.Zero), поэтому уже открытый FileShare.Read
                // хендл этому не мешает — Install.cs тому доказательство. Проверка подписи
                // бутстраппера выше не покрывает этот файл — оставляем обе проверки, а не
                // одну вместо другой (defense-in-depth, который явно требует план).
                var setupExeHandle = new FileStream(setupExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    if (!AuthenticodeVerifier.IsSignedByMicrosoft(setupExePath, out string setupSignatureError))
                    {
                        setupExeHandle.Dispose();
                        return FailedAndCleanup(workDir, $"Подпись setup.exe не подтверждена: {setupSignatureError}");
                    }
                }
                catch
                {
                    setupExeHandle.Dispose();
                    throw;
                }

                return OdtPrepareResult.Ok(setupExePath, workDir, setupExeHandle);
            }
            catch (OperationCanceledException)
            {
                // workDir НЕ удаляем: дочерний процесс (winget download / самораспаковка)
                // мог не успеть завершиться и всё ещё писать в эту папку — удаление могло
                // бы столкнуться с файлом, открытым на запись, или удалить папку из-под
                // живого процесса. Это единственный путь, где директория осознанно
                // остаётся — на все остальные ветки ниже это не распространяется.
                throw;
            }
            catch (Exception ex)
            {
                return FailedAndCleanup(workDir, $"Подготовка ODT: {ex.Message}");
            }
        }

        /// <summary>
        /// Помечает подготовку ODT неуспешной и рекурсивно удаляет её временную папку
        /// немедленно, синхронно, здесь же. На успешном пути workDir/setup.exe тоже не
        /// остаются ничьими: ими владеет возвращённый <see cref="OdtPrepareResult"/>
        /// (IDisposable) — вызывающий код обязан вызвать Dispose() после того, как
        /// элевированный процесс уже запущен (см. RunConfigureFlowAsync). А при ЛЮБОЙ
        /// неудаче здесь OdtPrepareResult несёт только текст ошибки — вызывающий код не
        /// узнаёт путь к workDir и не может убрать за собой, поэтому чистим сами: иначе
        /// каждая неудачная попытка (а именно неудачи чаще всего повторяют) оставляла бы
        /// в %TEMP% ~3.5 МБ бутстраппера навсегда. Удаление — в собственном try/catch,
        /// чтобы сбой очистки никогда не заслонил настоящую причину ошибки.
        /// </summary>
        private static OdtPrepareResult FailedAndCleanup(string workDir, string error)
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* очистка — best effort, не должна маскировать исходную ошибку */ }

            return OdtPrepareResult.Failed(error);
        }

        /// <summary>
        /// Запускает "&lt;exePath&gt; /configure &lt;configXmlPath&gt;" с повышением прав для
        /// свежераспакованного setup.exe (из PrepareAsync). Возвращает код возврата
        /// процесса; -1, если процесс не удалось запустить — в том числе если пользователь
        /// отклонил запрос UAC (это тоже "не удалось запустить", а не ошибка выполнения)
        /// и если <paramref name="exePath"/> пуст/не задан (тогда Process.Start бросил бы
        /// не Win32Exception, а InvalidOperationException — ловим это отдельно ниже, чтобы
        /// контракт "-1 при сбое запуска" был честным на каждом пути, а не только на тех,
        /// что сейчас гарантирует вызывающий код).
        ///
        /// ⚠️ Exit code реально означает завершение операции только для setup.exe ODT: он
        /// синхронно выполняет /configure и возвращает управление, когда всё сделано.
        /// Уже установленный OfficeClickToRun.exe сюда не передавать: C2R-клиент может
        /// отдать работу отдельному процессу и вернуться раньше фактического завершения
        /// (поэтому запасной путь через него убран из OfficeViewModel).
        ///
        /// ⚠️ ВАЖНО про <paramref name="ct"/>: отмена здесь ТОЛЬКО прекращает ожидание
        /// (await) в этом методе — она НЕ останавливает и не убивает уже запущенный
        /// повышенный процесс, который в этот момент может выполнять реальное необратимое
        /// удаление/изменение установки Office. "Отмена" в терминах этого метода — это
        /// "мы перестали ждать", а не "операция остановлена". Поэтому:
        /// вызывающий код НЕ ДОЛЖЕН освобождать InstallSemaphore по отмене этого вызова —
        /// элевированный процесс может продолжать работать над Office ещё долго после
        /// того, как этот метод перестал ждать, и до завершения нужно считать машину
        /// занятой (не простаивающей), иначе второй запуск может стартовать поверх
        /// наполовину удалённой установки. План фиксирует сигнатуру этого метода с
        /// параметром CancellationToken; сейчас Task 3 всегда передаёт
        /// CancellationToken.None, так что риск не проявляется — но если когда-нибудь
        /// сюда передадут реальный токен, прочитайте этот комментарий до конца, а не
        /// после инцидента.
        /// </summary>
        public async Task<int> RunConfigureAsync(string exePath, string configXmlPath, CancellationToken ct)
        {
            // Пустой/отсутствующий exePath — тоже "не удалось запустить": без этой
            // проверки Process.Start бросил бы InvalidOperationException, а не
            // Win32Exception, и метод нарушил бы собственный документированный
            // контракт "-1 при сбое запуска" (см. XML-doc выше).
            if (string.IsNullOrEmpty(exePath))
            {
                AppLogger.Write("RunConfigureAsync: exePath пуст — процесс не запущен");
                return -1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"/configure \"{configXmlPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                AppLogger.Write("RunConfigureAsync: пользователь отклонил запрос UAC — процесс не запущен");
                return -1;
            }
            catch (Win32Exception ex)
            {
                AppLogger.Write(ex, "RunConfigureAsync: не удалось запустить процесс");
                return -1;
            }

            using var _ = process;
            if (process == null) return -1;

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
    }
}
