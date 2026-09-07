using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// <para>
    /// Второе следствие этой изоляции — освобождение COM-объектов. Каждый вызов
    /// wuapi возвращает отдельный COM-объект (сессия, поисковик, результат поиска,
    /// КАЖДЫЙ IUpdate, его Identity/Categories/KBArticleIDs), и без явного
    /// освобождения все они живут до финализатора RCW: один поиск на реальной машине
    /// оставляет сотни висящих объектов, а клиент проверяет обновления в фоне
    /// регулярно. Поэтому всё, что получено из COM, освобождается в finally —
    /// строго от дочерних объектов к родительским и только после того, как данные из
    /// них скопированы в обычные C#-модели.
    /// </para>
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
            object? sysInfoCom = null;
            try
            {
                sysInfoCom = CreateComObject("Microsoft.Update.SystemInfo");
                dynamic sysInfo = sysInfoCom;
                return (bool)sysInfo.RebootRequired;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Проверка RebootRequired: {ex.Message}");
                return false; // fail-open здесь безопасен: хуже случай — попытка установки упадёт с понятной ошибкой API
            }
            finally
            {
                // Метод вызывается перед каждой установкой и при каждой проверке статуса,
                // то есть многократно за сессию — без освобождения каждый вызов оставлял
                // бы свой объект SystemInfo.
                ReleaseCom(sysInfoCom);
            }
        }

        public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct)
        {
            // COM-объекты Windows Update Agent требуют MTA-апартамент для надёжной
            // работы Search() в фоновом потоке — обычные потоки пула задач (Task.Run)
            // уже MTA по умолчанию в .NET, отдельный поток создавать не нужно.
            return Task.Run(() =>
            {
                object? sessionCom = null;
                object? searcherCom = null;
                object? resultCom = null;
                object? updatesCom = null;
                try
                {
                    sessionCom = CreateComObject("Microsoft.Update.Session");
                    dynamic session = sessionCom;
                    dynamic searcher = session.CreateUpdateSearcher();
                    searcherCom = searcher;

                    ct.ThrowIfCancellationRequested();
                    dynamic result = searcher.Search(SearchCriteria);
                    resultCom = result;

                    int resultCode = (int)result.ResultCode;
                    // OperationResultCode: 0=NotStarted,1=InProgress,2=Succeeded,3=SucceededWithErrors,4=Failed,5=Aborted
                    if (resultCode is 4 or 5)
                        return WindowsUpdateSearchResult.Failed(
                            $"Поиск обновлений завершился неудачно (код {resultCode}).");

                    dynamic updates = result.Updates;
                    updatesCom = updates;
                    int count = (int)updates.Count;
                    var items = new List<WindowsUpdateItem>(count);

                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        dynamic u = updates.Item(i);
                        // MapToItem копирует все нужные поля в обычный WindowsUpdateItem,
                        // после чего сам IUpdate не нужен. Патчей в выдаче бывают сотни —
                        // держать их до сборки мусора незачем.
                        try { items.Add(MapToItem(u)); }
                        finally { ReleaseCom((object)u); }
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
                finally
                {
                    // От дочерних к родительским: коллекция получена из результата,
                    // результат — из поисковика, поисковик — из сессии.
                    ReleaseCom(updatesCom);
                    ReleaseCom(resultCom);
                    ReleaseCom(searcherCom);
                    ReleaseCom(sessionCom);
                }
            }, ct);
        }

        private static WindowsUpdateItem MapToItem(dynamic u)
        {
            var categoryNames = new List<string>();
            object? categoriesCom = null;
            try
            {
                dynamic categories = u.Categories;
                categoriesCom = categories;
                int catCount = (int)categories.Count;
                for (int i = 0; i < catCount; i++)
                {
                    // Item(i) здесь возвращает ICategory — тоже COM-объект на каждую
                    // категорию каждого патча.
                    dynamic category = categories.Item(i);
                    try { categoryNames.Add((string)category.Name); }
                    finally { ReleaseCom((object)category); }
                }
            }
            finally { ReleaseCom(categoriesCom); }

            var kbIds = new List<string>();
            object? kbArticlesCom = null;
            try
            {
                dynamic kbArticles = u.KBArticleIDs;
                kbArticlesCom = kbArticles;
                int kbCount = (int)kbArticles.Count;
                // В отличие от Categories, Item(i) у IStringCollection возвращает строку,
                // а не COM-объект — освобождать поэлементно нечего.
                for (int i = 0; i < kbCount; i++)
                    kbIds.Add((string)kbArticles.Item(i));
            }
            finally { ReleaseCom(kbArticlesCom); }

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
            // MsrcSeverity заполнена только у обновлений безопасности — её отсутствие
            // штатно, поэтому просто оставляем пустую строку.
            try { severity = (string)u.MsrcSeverity ?? ""; } catch { }

            return new WindowsUpdateItem
            {
                UpdateId = ReadUpdateId(u),
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

        /// <summary>
        /// Читает UpdateID патча. Отдельный метод, потому что u.Identity — это не поле,
        /// а ещё один COM-объект (IUpdateIdentity): выражение вида u.Identity.UpdateID
        /// оставляет по висящему объекту на каждое чтение, а читается идентификатор в
        /// каждом цикле по результатам поиска.
        /// </summary>
        private static string ReadUpdateId(dynamic u)
        {
            object? identityCom = null;
            try
            {
                dynamic identity = u.Identity;
                identityCom = identity;
                return (string)identity.UpdateID;
            }
            finally { ReleaseCom(identityCom); }
        }

        /// <summary>
        /// Детерминированно освобождает COM-объект. Проверка IsComObject нужна, потому
        /// что часть значений из dynamic-вызовов — обычные .NET-объекты (строки, числа),
        /// а FinalReleaseComObject на таком значении бросает исключение. Сама ошибка
        /// освобождения глушится сознательно: она не должна ронять операцию обновления,
        /// ради которой объект и создавался.
        /// </summary>
        private static void ReleaseCom(object? comObject)
        {
            if (comObject is null || !Marshal.IsComObject(comObject)) return;
            try { Marshal.FinalReleaseComObject(comObject); } catch { }
        }

        private static object CreateComObject(string progId)
        {
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException($"COM-класс {progId} не зарегистрирован в системе.");
            return Activator.CreateInstance(type)!;
        }

        private static bool TryGetHResult(Exception ex, out int hresult)
        {
            hresult = ex.HResult;
            return ex is COMException;
        }

        /// <summary>
        /// Итог общей для InstallAsync/DownloadOnlyAsync фазы «найти → сопоставить по ID →
        /// принять EULA → проверить место → скачать». COM-объекты отдаются наружу как
        /// object, а не dynamic: поле типа dynamic с null-значением ругается на
        /// nullable-аннотации, а вызывающему коду всё равно нужен явный каст к dynamic.
        /// <para>
        /// Здесь же собраны и промежуточные объекты фазы (поисковик, результат поиска,
        /// загрузчик): наружу они не нужны, но освободить их можно только после того,
        /// как вызывающий код закончил работать с Matched — элементы получены из
        /// результата поиска и используются до самого конца установки. Поэтому объект
        /// фазы создаётся вызывающим кодом и освобождается им же в finally
        /// (ReleaseComObjects), в том числе на неуспешном пути, где создать успели не всё.
        /// </para>
        /// </summary>
        private sealed class DownloadPhaseResult
        {
            public bool Success { get; private set; }
            public string ErrorMessage { get; private set; } = "";
            /// <summary>Найденные и принятые к скачиванию IUpdate — в порядке добавления в коллекцию.</summary>
            public List<dynamic> Matched { get; } = new();
            /// <summary>Microsoft.Update.UpdateColl с теми же патчами — для installer.Updates.</summary>
            public object? Collection { get; set; }
            /// <summary>IDownloadResult — для поэлементных итогов через GetUpdateResult(i).</summary>
            public object? DownloadResult { get; set; }
            /// <summary>IUpdateSearcher фазы.</summary>
            public object? Searcher { get; set; }
            /// <summary>ISearchResult повторного поиска.</summary>
            public object? SearchResult { get; set; }
            /// <summary>IUpdateCollection из результата поиска — родитель элементов Matched.</summary>
            public object? FoundUpdates { get; set; }
            /// <summary>IUpdateDownloader фазы.</summary>
            public object? Downloader { get; set; }

            public void Fail(string message)
            {
                Success = false;
                ErrorMessage = message;
            }

            public void Complete() => Success = true;

            /// <summary>
            /// Освобождает все COM-объекты фазы. Порядок строго от дочерних к
            /// родительским: результат скачивания получен из загрузчика, загрузчик
            /// держит коллекцию, элементы Matched получены из результата поиска.
            /// Вызывать только тогда, когда работа с Matched полностью закончена —
            /// обращение к уже освобождённому объекту даст InvalidComObjectException.
            /// </summary>
            public void ReleaseComObjects()
            {
                foreach (object u in Matched) ReleaseCom(u);
                Matched.Clear();

                ReleaseCom(DownloadResult); DownloadResult = null;
                ReleaseCom(Downloader); Downloader = null;
                ReleaseCom(Collection); Collection = null;
                ReleaseCom(FoundUpdates); FoundUpdates = null;
                ReleaseCom(SearchResult); SearchResult = null;
                ReleaseCom(Searcher); Searcher = null;
            }
        }

        /// <summary>
        /// Общая фаза скачивания. Выделена из InstallAsync, чтобы фоновая загрузка
        /// (DownloadOnlyAsync) использовала ровно тот же путь со всеми проверками
        /// безопасности — повторный поиск по ID, принятие EULA, оценка свободного места —
        /// и они не разъехались между двумя копиями кода. Установщик здесь не создаётся
        /// и не вызывается: за установку отвечает только InstallAsync после этой фазы.
        /// Синхронная — вызывается уже внутри Task.Run вызывающего метода (MTA-апартамент,
        /// см. заметку в SearchAsync).
        /// <para>
        /// Результат заполняется в переданный phase, а не возвращается: созданные по ходу
        /// COM-объекты должны быть доступны вызывающему коду для освобождения даже если
        /// фаза оборвётся исключением на середине.
        /// </para>
        /// </summary>
        private static void RunDownloadPhase(
            object sessionCom,
            DownloadPhaseResult phase,
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            dynamic session = sessionCom;
            dynamic searcher = session.CreateUpdateSearcher();
            phase.Searcher = searcher;

            ct.ThrowIfCancellationRequested();
            // Повторный поиск — не доверяем списку ID вслепую (см. заметку безопасности выше).
            dynamic searchResult = searcher.Search(SearchCriteria);
            phase.SearchResult = searchResult;
            dynamic allFound = searchResult.Updates;
            phase.FoundUpdates = allFound;
            int foundCount = (int)allFound.Count;

            dynamic updatesToDownload = Activator.CreateInstance(
                Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            phase.Collection = updatesToDownload;

            var matched = phase.Matched;
            for (int i = 0; i < foundCount; i++)
            {
                dynamic u = allFound.Item(i);
                bool keep = false;
                try
                {
                    string id = ReadUpdateId(u);
                    if (!updateIds.Contains(id)) continue;

                    // EULA принимается прямо перед добавлением в очередь на скачивание —
                    // чекбокс в UI уже подразумевает согласие (текст лицензии был показан
                    // в диалоге подтверждения перед стартом, см. Task 12). В фоновом режиме
                    // согласие даётся один раз — самим выбором режима «скачивать в фоне»:
                    // принятие EULA ничего не устанавливает и не меняет систему, а установка
                    // по-прежнему требует отдельного явного клика с показом текста лицензии.
                    try { if (!(bool)u.EulaAccepted) u.AcceptEula(); }
                    catch (Exception ex) { AppLogger.Write($"[WindowsUpdateComSource] AcceptEula({id}): {ex.Message}"); }

                    matched.Add(u);
                    updatesToDownload.Add(u);
                    keep = true;
                }
                finally
                {
                    // Патчи, не попавшие в выборку, освобождаются сразу: в выдаче их
                    // обычно на порядок больше выбранных. Попавшие в matched живут до
                    // конца операции (по ним идёт установка и разбор поэлементных итогов)
                    // и освобождаются вызывающим кодом в ReleaseComObjects.
                    if (!keep) ReleaseCom((object)u);
                }
            }

            if (matched.Count == 0)
            {
                phase.Fail("Выбранные патчи больше не предлагаются сервером обновлений — попробуйте обновить список.");
                return;
            }

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
                {
                    phase.Fail(
                        $"Недостаточно места на диске {systemDrive} — нужно ориентировочно {Helpers.SizeFormatter.BytesToMBWhole(totalDownloadBytes * 2)} свободных, доступно {Helpers.SizeFormatter.BytesToMBWhole(drive.AvailableFreeSpace)}.");
                    return;
                }
            }

            // ── Скачивание ──
            dynamic downloader = session.CreateUpdateDownloader();
            phase.Downloader = downloader;
            downloader.Updates = updatesToDownload;

            progress.Report(new WindowsUpdateProgress
            {
                Phase = "Скачивание", CompletedCount = 0, TotalCount = matched.Count, PercentComplete = 0
            });
            dynamic downloadResult = downloader.Download();
            phase.DownloadResult = downloadResult;
            int downloadCode = (int)downloadResult.ResultCode;
            if (downloadCode is 4 or 5)
            {
                phase.Fail($"Скачивание обновлений завершилось неудачно (код {downloadCode}).");
                return;
            }

            phase.Complete();
        }

        public Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                object? sessionCom = null;
                object? installerCom = null;
                object? installResultCom = null;
                var phase = new DownloadPhaseResult();
                try
                {
                    sessionCom = CreateComObject("Microsoft.Update.Session");
                    dynamic session = sessionCom;

                    RunDownloadPhase(sessionCom, phase, updateIds, progress, ct);
                    if (!phase.Success)
                        return new WindowsUpdateInstallOutcome
                        {
                            Success = false,
                            ErrorMessage = phase.ErrorMessage
                        };

                    var matched = phase.Matched;
                    dynamic updatesToInstall = phase.Collection!;

                    ct.ThrowIfCancellationRequested();

                    // ── Установка ──
                    dynamic installer = session.CreateUpdateInstaller();
                    installerCom = installer;
                    installer.Updates = updatesToInstall;

                    progress.Report(new WindowsUpdateProgress
                    {
                        Phase = "Установка", CompletedCount = 0, TotalCount = matched.Count, PercentComplete = 0
                    });
                    dynamic installResult = installer.Install();
                    installResultCom = installResult;

                    var itemOutcomes = new List<WindowsUpdateItemOutcome>();
                    for (int i = 0; i < matched.Count; i++)
                    {
                        dynamic u = matched[i];
                        // GetUpdateResult(i) отдаёт отдельный IUpdateInstallationResult на
                        // каждый патч — освобождаем в конце итерации, данные из него уже
                        // переписаны в WindowsUpdateItemOutcome. Сам u — из phase.Matched,
                        // его освобождает ReleaseComObjects после цикла.
                        object? perUpdateResultCom = null;
                        try
                        {
                            dynamic perUpdateResult = installResult.GetUpdateResult(i);
                            perUpdateResultCom = perUpdateResult;
                            int code = (int)perUpdateResult.ResultCode;
                            bool ok = code == 2 || code == 3; // Succeeded или SucceededWithErrors
                            itemOutcomes.Add(new WindowsUpdateItemOutcome
                            {
                                UpdateId = ReadUpdateId(u),
                                Title = (string)u.Title,
                                Success = ok,
                                ErrorMessage = ok ? "" : WindowsUpdateErrorMapper.MapHResult((int)perUpdateResult.HResult)
                            });
                        }
                        finally { ReleaseCom(perUpdateResultCom); }

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
                    // Пустой catch здесь означал бы «перезагрузка не нужна» при том, что
                    // прочитать флаг просто не удалось — пользователь остался бы с
                    // недоустановленными патчами. Пишем причину так же, как в CheckRebootRequired.
                    try { overallRebootRequired = (bool)installResult.RebootRequired; }
                    catch (Exception ex) { AppLogger.Write($"[WindowsUpdateComSource] Чтение RebootRequired после установки: {ex.Message}"); }

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
                finally
                {
                    // От дочерних к родительским: итог установки → установщик → объекты
                    // фазы (найденные патчи, коллекция, загрузчик, поиск) → сессия.
                    ReleaseCom(installResultCom);
                    ReleaseCom(installerCom);
                    phase.ReleaseComObjects();
                    ReleaseCom(sessionCom);
                }
            }, ct);
        }

        /// <summary>
        /// Тихое скачивание патчей без установки. Проходит ровно ту же фазу, что и
        /// InstallAsync (RunDownloadPhase), и на этом останавливается: ни
        /// CreateUpdateInstaller, ни Install здесь не вызываются и вызываться не должны —
        /// система после этого метода не меняется, файлы просто лежат в кэше Windows Update.
        /// Скачанные патчи WUA помечает у себя (IUpdate.IsDownloaded = true) на уровне ОС,
        /// а не процесса, поэтому пометка переживает завершение COM-сессии: следующий
        /// IUpdateInstaller.Install() по такому патчу не качает его заново и стартует сразу —
        /// отдельная проверка «пропустить скачивание, если уже скачано» в InstallAsync
        /// не нужна, этим занимается сам агент обновлений.
        /// </summary>
        public Task<WindowsUpdateDownloadOutcome> DownloadOnlyAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                object? sessionCom = null;
                var phase = new DownloadPhaseResult();
                try
                {
                    sessionCom = CreateComObject("Microsoft.Update.Session");

                    RunDownloadPhase(sessionCom, phase, updateIds, progress, ct);
                    if (!phase.Success)
                        return new WindowsUpdateDownloadOutcome
                        {
                            ErrorMessage = phase.ErrorMessage
                        };

                    var matched = phase.Matched;
                    dynamic downloadResult = phase.DownloadResult!;

                    var itemOutcomes = new List<WindowsUpdateItemOutcome>();
                    for (int i = 0; i < matched.Count; i++)
                    {
                        dynamic u = matched[i];
                        // Поэлементный IDownloadResult — отдельный COM-объект на патч.
                        object? perUpdateResultCom = null;
                        try
                        {
                            dynamic perUpdateResult = downloadResult.GetUpdateResult(i);
                            perUpdateResultCom = perUpdateResult;
                            int code = (int)perUpdateResult.ResultCode;
                            bool ok = code == 2 || code == 3; // Succeeded или SucceededWithErrors
                            itemOutcomes.Add(new WindowsUpdateItemOutcome
                            {
                                UpdateId = ReadUpdateId(u),
                                Title = (string)u.Title,
                                Success = ok,
                                ErrorMessage = ok ? "" : WindowsUpdateErrorMapper.MapHResult((int)perUpdateResult.HResult)
                            });
                        }
                        finally { ReleaseCom(perUpdateResultCom); }

                        progress.Report(new WindowsUpdateProgress
                        {
                            Phase = "Скачивание",
                            CurrentTitle = (string)u.Title,
                            CompletedCount = i + 1,
                            TotalCount = matched.Count,
                            PercentComplete = (int)((i + 1) * 100.0 / matched.Count)
                        });
                    }

                    return new WindowsUpdateDownloadOutcome
                    {
                        Items = itemOutcomes
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TryGetHResult(ex, out int hr))
                {
                    return new WindowsUpdateDownloadOutcome { ErrorMessage = WindowsUpdateErrorMapper.MapHResult(hr) };
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdateComSource] DownloadOnlyAsync: {ex}");
                    return new WindowsUpdateDownloadOutcome { ErrorMessage = $"Ошибка скачивания: {ex.Message}" };
                }
                finally
                {
                    // Результат скачивания и загрузчик принадлежат фазе — их освобождает
                    // она же, после найденных патчей; сессия закрывается последней.
                    phase.ReleaseComObjects();
                    ReleaseCom(sessionCom);
                }
            }, ct);
        }
    }
}
