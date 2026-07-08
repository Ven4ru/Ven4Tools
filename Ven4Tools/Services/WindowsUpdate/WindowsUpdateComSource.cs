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
            catch
            {
                // Не удалось прочитать поля EULA — fail-safe: считаем лицензию непринятой,
                // чтобы патч точно попал в диалог подтверждения, а не был случайно пропущен.
                eulaAccepted = false;
                eulaText = "Не удалось получить текст лицензионного соглашения для этого патча — проверьте вручную перед установкой.";
            }

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

        public Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    dynamic session = CreateComObject("Microsoft.Update.Session");
                    dynamic searcher = session.CreateUpdateSearcher();

                    ct.ThrowIfCancellationRequested();
                    // Повторный поиск — не доверяем списку ID вслепую (см. заметку безопасности выше).
                    dynamic searchResult = searcher.Search(SearchCriteria);
                    dynamic allFound = searchResult.Updates;
                    int foundCount = (int)allFound.Count;

                    dynamic updatesToInstall = Activator.CreateInstance(
                        Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;

                    var matched = new List<dynamic>();
                    for (int i = 0; i < foundCount; i++)
                    {
                        dynamic u = allFound.Item(i);
                        string id = (string)u.Identity.UpdateID;
                        if (!updateIds.Contains(id)) continue;

                        // EULA принимается прямо перед добавлением в очередь на скачивание —
                        // чекбокс в UI уже подразумевает согласие (текст лицензии был показан
                        // в диалоге подтверждения перед стартом, см. Task 12).
                        try { if (!(bool)u.EulaAccepted) u.AcceptEula(); }
                        catch (Exception ex) { AppLogger.Write($"[WindowsUpdateComSource] AcceptEula({id}): {ex.Message}"); }

                        matched.Add(u);
                        updatesToInstall.Add(u);
                    }

                    if (matched.Count == 0)
                        return new WindowsUpdateInstallOutcome
                        {
                            Success = false,
                            ErrorMessage = "Выбранные патчи больше не предлагаются сервером обновлений — попробуйте обновить список."
                        };

                    // ── Проверка места на диске (перед стартом скачивания) ──
                    long totalDownloadBytes = 0;
                    foreach (var u in matched)
                    {
                        try { totalDownloadBytes += (long)u.MaxDownloadSize; } catch { /* поле не всегда доступно — тогда просто не учитываем в оценке */ }
                    }
                    if (totalDownloadBytes > 0)
                    {
                        string systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
                        var drive = new DriveInfo(systemDrive);
                        // Запас x2 сверх заявленного размера — распаковка/установка временно
                        // занимает больше места, чем сам скачанный пакет.
                        if (drive.AvailableFreeSpace < totalDownloadBytes * 2)
                            return new WindowsUpdateInstallOutcome
                            {
                                Success = false,
                                ErrorMessage = $"Недостаточно места на диске {systemDrive} — нужно ориентировочно {totalDownloadBytes * 2 / 1024 / 1024} МБ свободных, доступно {drive.AvailableFreeSpace / 1024 / 1024} МБ."
                            };
                    }

                    // ── Скачивание ──
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = updatesToInstall;

                    progress.Report(new WindowsUpdateProgress
                    {
                        Phase = "Скачивание", CompletedCount = 0, TotalCount = matched.Count, PercentComplete = 0
                    });
                    dynamic downloadResult = downloader.Download();
                    int downloadCode = (int)downloadResult.ResultCode;
                    if (downloadCode is 4 or 5)
                        return new WindowsUpdateInstallOutcome
                        {
                            Success = false,
                            ErrorMessage = $"Скачивание обновлений завершилось неудачно (код {downloadCode})."
                        };

                    ct.ThrowIfCancellationRequested();

                    // ── Установка ──
                    dynamic installer = session.CreateUpdateInstaller();
                    installer.Updates = updatesToInstall;

                    progress.Report(new WindowsUpdateProgress
                    {
                        Phase = "Установка", CompletedCount = 0, TotalCount = matched.Count, PercentComplete = 0
                    });
                    dynamic installResult = installer.Install();

                    var itemOutcomes = new List<WindowsUpdateItemOutcome>();
                    for (int i = 0; i < matched.Count; i++)
                    {
                        dynamic u = matched[i];
                        dynamic perUpdateResult = installResult.GetUpdateResult(i);
                        int code = (int)perUpdateResult.ResultCode;
                        bool ok = code == 2 || code == 3; // Succeeded или SucceededWithErrors
                        itemOutcomes.Add(new WindowsUpdateItemOutcome
                        {
                            UpdateId = (string)u.Identity.UpdateID,
                            Title = (string)u.Title,
                            Success = ok,
                            ErrorMessage = ok ? "" : WindowsUpdateErrorMapper.MapHResult((int)perUpdateResult.HResult)
                        });
                        progress.Report(new WindowsUpdateProgress
                        {
                            Phase = "Установка",
                            CurrentTitle = (string)u.Title,
                            CompletedCount = i + 1,
                            TotalCount = matched.Count,
                            PercentComplete = (int)((i + 1) * 100.0 / matched.Count)
                        });
                    }

                    bool overallRebootRequired = false;
                    try { overallRebootRequired = (bool)installResult.RebootRequired; } catch { }

                    return new WindowsUpdateInstallOutcome
                    {
                        Success = itemOutcomes.All(o => o.Success),
                        Items = itemOutcomes,
                        RebootRequired = overallRebootRequired
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TryGetHResult(ex, out int hr))
                {
                    return new WindowsUpdateInstallOutcome { Success = false, ErrorMessage = WindowsUpdateErrorMapper.MapHResult(hr) };
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdateComSource] InstallAsync: {ex}");
                    return new WindowsUpdateInstallOutcome { Success = false, ErrorMessage = $"Ошибка установки: {ex.Message}" };
                }
            }, ct);
        }
    }
}
