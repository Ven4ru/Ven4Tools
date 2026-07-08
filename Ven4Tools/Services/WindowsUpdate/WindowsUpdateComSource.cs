using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>
    /// Реализация IWindowsUpdateSource поверх нативного Windows Update Agent COM API
    /// (wuapi.dll). Единственное место в проекте, где используется dynamic/COM —
    /// специально изолировано, чтобы риск опечатки в имени члена (RuntimeBinderException,
    /// ловится только в рантайме) не расползался по остальному коду.
    /// </summary>
    public sealed class WindowsUpdateComSource : IWindowsUpdateSource
    {
        // Критерий поиска: все не установленные и не скрытые пользователем обновления
        // всех типов (Software покрывает кумулятивные/security/driver/feature — драйверы
        // в API относятся к Type='Software' с категорией "Drivers", отдельного Type для
        // них нет). IsHidden=0 — не показываем то, что пользователь явно скрыл в прошлом
        // через штатный Windows Update (у нас нет своего UI для "скрыть", поэтому уважаем
        // выбор, сделанный там).
        private const string SearchCriteria = "IsInstalled=0 and IsHidden=0";

        public bool IsServiceRunning()
        {
            try
            {
                using var sc = new ServiceController("wuauserv");
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Проверка службы: {ex.Message}");
                return false;
            }
        }

        public bool TryStartService()
        {
            try
            {
                using var sc = new ServiceController("wuauserv");
                if (sc.Status == ServiceControllerStatus.Running) return true;
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Запуск службы не удался: {ex.Message}");
                return false;
            }
        }

        public bool IsRebootPending()
        {
            try
            {
                dynamic sysInfo = CreateComObject("Microsoft.Update.SystemInfo");
                return (bool)sysInfo.RebootRequired;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Проверка RebootRequired: {ex.Message}");
                return false; // fail-open здесь безопасен: хуже случай — попытка установки упадёт с понятной ошибкой API
            }
        }

        public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct)
        {
            // COM-объекты Windows Update Agent требуют MTA-апартамент для надёжной
            // работы Search() в фоновом потоке — обычные потоки пула задач (Task.Run)
            // уже MTA по умолчанию в .NET, отдельный поток создавать не нужно.
            return Task.Run(() =>
            {
                try
                {
                    dynamic session = CreateComObject("Microsoft.Update.Session");
                    dynamic searcher = session.CreateUpdateSearcher();

                    ct.ThrowIfCancellationRequested();
                    dynamic result = searcher.Search(SearchCriteria);

                    int resultCode = (int)result.ResultCode;
                    // OperationResultCode: 0=NotStarted,1=InProgress,2=Succeeded,3=SucceededWithErrors,4=Failed,5=Aborted
                    if (resultCode is 4 or 5)
                        return WindowsUpdateSearchResult.Failed(
                            $"Поиск обновлений завершился неудачно (код {resultCode}).");

                    dynamic updates = result.Updates;
                    int count = (int)updates.Count;
                    var items = new List<WindowsUpdateItem>(count);

                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        dynamic u = updates.Item(i);
                        items.Add(MapToItem(u));
                    }

                    return WindowsUpdateSearchResult.Ok(items);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TryGetHResult(ex, out int hr))
                {
                    return WindowsUpdateSearchResult.Failed(WindowsUpdateErrorMapper.MapHResult(hr));
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdateComSource] Search: {ex}");
                    return WindowsUpdateSearchResult.Failed(
                        $"Не удалось выполнить поиск обновлений: {ex.Message}");
                }
            }, ct);
        }

        private static WindowsUpdateItem MapToItem(dynamic u)
        {
            var categoryNames = new List<string>();
            dynamic categories = u.Categories;
            int catCount = (int)categories.Count;
            for (int i = 0; i < catCount; i++)
                categoryNames.Add((string)categories.Item(i).Name);

            var kbIds = new List<string>();
            dynamic kbArticles = u.KBArticleIDs;
            int kbCount = (int)kbArticles.Count;
            for (int i = 0; i < kbCount; i++)
                kbIds.Add((string)kbArticles.Item(i));

            long sizeBytes = 0;
            try { sizeBytes = (long)u.MaxDownloadSize; } catch { /* поле не всегда доступно — не критично */ }

            string eulaText = "";
            bool eulaAccepted = true;
            try
            {
                eulaAccepted = (bool)u.EulaAccepted;
                eulaText = eulaAccepted ? "" : (string)u.EulaText;
            }
            catch { /* не у всех патчей вообще есть EULA-поля */ }

            string severity = "";
            try { severity = (string)u.MsrcSeverity ?? ""; } catch { }

            return new WindowsUpdateItem
            {
                UpdateId = (string)u.Identity.UpdateID,
                Title = (string)u.Title,
                CategoryNames = categoryNames,
                KbArticleIds = kbIds,
                SizeBytes = sizeBytes,
                Severity = severity,
                IsDownloaded = (bool)u.IsDownloaded,
                EulaAccepted = eulaAccepted,
                EulaText = eulaText
            };
        }

        private static dynamic CreateComObject(string progId)
        {
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException($"COM-класс {progId} не зарегистрирован в системе.");
            return Activator.CreateInstance(type)!;
        }

        private static bool TryGetHResult(Exception ex, out int hresult)
        {
            hresult = ex.HResult;
            return ex is System.Runtime.InteropServices.COMException;
        }

        // Реализация Download/Install — Task 8.
        public Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct) =>
            throw new NotImplementedException("Реализуется в Task 8");
    }
}
