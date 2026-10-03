using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ven4Tools.Services;
using Ven4Tools.Shared;
using Ven4Tools.Views;

namespace Ven4Tools
{
    public partial class App : Application
    {
        private static HeartbeatService? _heartbeat;
        private static UpdateBackgroundService? _updateBgService;
        private static WindowsUpdateBackgroundService? _windowsUpdateBgService;
        private static ClientControlServer? _clientControlServer;
        private static Mutex? _instanceMutex;

        /// <summary>Задание на установку набора из командной строки; null — обычный запуск.</summary>
        public static UnattendedRequest? Unattended { get; private set; }

        /// <summary>Тихий режим: окно свёрнуто, вопросов нет, по окончании клиент завершается.</summary>
        public static bool IsSilentRun => Unattended?.Silent == true;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Тёмный системный заголовок на всех окнах — иначе native title bar
            // остаётся светлым поверх тёмной темы приложения даже при тёмной теме Windows.
            try { WindowChromeHelper.RegisterGlobalDarkTitleBar(); } catch { }

            // Задание тихого режима разбирается до всего остального: ошибка в нём не
            // должна заканчиваться открытым окном, которого сценарий установки не ждёт.
            bool silentRequested = Array.Exists(e.Args, a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));
            var parseStatus = UnattendedCommandLine.Parse(
                e.Args, System.IO.File.ReadAllText, out var unattended, out string parseError);
            if (parseStatus == UnattendedCommandLine.ParseStatus.Error)
            {
                AppLogger.Write($"[App] Задание на установку не разобрано: {parseError}");
                if (!silentRequested)
                    MessageBox.Show(parseError, "Ven4Tools — установка по заданию", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(UnattendedExitCode.InvalidRequest);
                return;
            }
            Unattended = unattended;

            // Единственный экземпляр клиента: два процесса гонялись бы за файлами
            // (profile.json, apps.json) и могли запустить параллельные установки.
            _instanceMutex = new Mutex(true, "Ven4Tools.Client.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                if (IsSilentRun)
                    AppLogger.Write("[App] Тихая установка не начата: клиент уже запущен");
                else
                    MessageBox.Show(
                        "Приложение Ven4Tools уже запущено.",
                        "Уже запущено",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown(IsSilentRun ? UnattendedExitCode.AlreadyRunning : 0);
                return;
            }

            // base.OnStartup поднимает событие Startup → выполняется App_Startup (см. App.xaml).
            base.OnStartup(e);
        }

        private async void App_Startup(object sender, StartupEventArgs e)
        {
            // Верхнеуровневый async void — любое необработанное исключение здесь
            // убивает процесс молча. Оборачиваем всё в try/catch и гарантируем,
            // что splash всегда закрывается, а при фатальной ошибке приложение
            // не зависает с висящим splash без главного окна.
            SplashWindow? splash = null;
            try
            {
                // Эти вызовы не должны валить старт — каждый best-effort, но сбой
                // молча лишает пользователя локализации/темы/восстановленного
                // региона на весь сеанс — без записи в журнал причину не найти.
                try { LocalizationService.Init(); } catch (Exception ex) { AppLogger.Write(ex, "[App] Не удалось инициализировать локализацию"); }
                try { ThemeService.Apply(ProfileService.Current.Theme); } catch (Exception ex) { AppLogger.Write(ex, "[App] Не удалось применить тему"); }
                try { _heartbeat = new HeartbeatService(); } catch { }
                // Регион Windows, подменённый вкладкой «Office» на время загрузки
                // установщика, возвращаем на старте: если прошлый сеанс убили в
                // середине, штатное восстановление в finally не отработало. Вкладка
                // создаётся лениво (а без интернета её кнопка вообще скрыта), поэтому
                // страховка обязана жить здесь, а не в её конструкторе. Молчаливый
                // провал здесь = регион пользователя остаётся подменённым без следа
                // в журнале, диагностировать нечем.
                try { OfficeRegionRecoveryService.Recover(); } catch (Exception ex) { AppLogger.Write(ex, "[App] Не удалось восстановить регион Windows после Office"); }
                // Краш-репорт прошлого сеанса отправляется только с явного согласия пользователя.
                // В тихом режиме спросить некого — вопрос откладывается до обычного запуска.
                if (!IsSilentRun)
                {
                    try { AskAndSendPendingCrashReport(); } catch { }
                }
                // Отправка отложенного отзыва — тоже fire-and-forget
                try { _ = FeedbackService.TrySendPendingAsync(); } catch { }

                try
                {
                    splash = new SplashWindow();
                    if (!IsSilentRun) splash.Show();
                    await splash.RunPreloadAsync();
                }
                catch (Exception ex)
                {
                    // Splash/preload — необязательная фаза, продолжаем старт, но
                    // без записи причина сбоя (например, повреждённый кеш) не найдётся.
                    AppLogger.Write(ex, "[App] Splash/предзагрузка завершились с ошибкой, продолжаем старт");
                }

                var main = new MainWindow();
                if (IsSilentRun)
                {
                    // Окно остаётся в панели задач свёрнутым: ход установки можно
                    // посмотреть, но фокус у пользователя оно не забирает.
                    main.ShowActivated = false;
                    main.WindowState = WindowState.Minimized;
                }
                main.Show();

                // Launcher работает без повышения прав, а клиент — elevated, поэтому
                // оконные сообщения от launcher блокируются UIPI. Именованный pipe,
                // доступный только текущему пользователю, служит безопасным каналом
                // запроса штатного закрытия между разными уровнями целостности.
                _clientControlServer = new ClientControlServer(() =>
                    Dispatcher.BeginInvoke(new Action(main.Close)));
                _clientControlServer.Start();

                // Фоновые уведомления об обновлениях/новых приложениях — после показа
                // окна, чтобы трей-иконка успела зарегистрироваться. Старт не блокирует.
                try
                {
                    _updateBgService = new UpdateBackgroundService();
                    _updateBgService.Start();
                }
                catch (Exception ex)
                {
                    // Сбой здесь молча лишает пользователя фоновых уведомлений
                    // об обновлениях на весь сеанс — без записи в журнал причину не найти.
                    AppLogger.Write(ex, "[App] Не удалось запустить фоновую проверку обновлений приложений");
                }

                try
                {
                    _windowsUpdateBgService = new WindowsUpdateBackgroundService();
                    _windowsUpdateBgService.Start();
                }
                catch (Exception ex)
                {
                    AppLogger.Write(ex, "[App] Не удалось запустить фоновую проверку обновлений Windows");
                }

                if (Unattended != null)
                    _ = RunUnattendedAsync(main, Unattended);
                else
                    OfferResumePendingInstall(main);
            }
            catch (Exception ex)
            {
                try { CrashReportService.Write(ex); } catch { }
                try
                {
                    MessageBox.Show(
                        "Не удалось запустить Ven4Tools.\n\n" + ex.Message,
                        "Ошибка запуска",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { }
                Shutdown(-1);
            }
            finally
            {
                // splash закрываем в любом случае — даже если MainWindow упал до Show().
                try { splash?.Close(); } catch { }
            }
        }

        /// <summary>
        /// Установка набора по заданию из командной строки. В тихом режиме по её
        /// окончании клиент завершается с кодом возврата; с окном — остаётся открытым.
        /// </summary>
        private async System.Threading.Tasks.Task RunUnattendedAsync(MainWindow main, UnattendedRequest request)
        {
            UnattendedReport report;
            try
            {
                report = request.UpdateApps
                    ? await RunUpdateAppsAsync()
                    : await main.RunUnattendedAsync(request);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "[App] Установка по заданию прервана ошибкой");
                report = new UnattendedReport
                {
                    ExitCode = UnattendedExitCode.PartialFailure,
                    Message = "установка прервана ошибкой: " + ex.Message,
                    FinishedUtc = DateTime.UtcNow.ToString("o")
                };
            }

            if (!string.IsNullOrWhiteSpace(request.ReportPath))
            {
                try
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    System.IO.File.WriteAllText(request.ReportPath, json);
                }
                catch (Exception ex)
                {
                    AppLogger.Write(ex, "[App] Не удалось записать итог установки по заданию");
                }
            }

            if (request.Silent) Shutdown(report.ExitCode);
        }

        /// <summary>
        /// Прошлая установка набора не дошла до конца (перезагрузка, закрытие, сбой) —
        /// спрашиваем, ставить ли оставшееся. Без согласия ничего не устанавливается,
        /// а очередь забывается, чтобы вопрос не повторялся при каждом запуске.
        /// </summary>
        private void OfferResumePendingInstall(MainWindow main)
        {
            var pending = PendingInstallQueue.Default.Load(DateTime.UtcNow);
            if (pending == null) return;

            var answer = MessageBox.Show(main,
                "В прошлый раз установка набора не была завершена.\n\n" +
                $"Осталось установить: {pending.AppIds.Count} ({DescribeIds(pending.AppIds)}).\n\n" +
                "Продолжить установку?",
                "Ven4Tools — незавершённая установка",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                PendingInstallQueue.Default.Clear();
                AppLogger.Write("[App] Незавершённая установка набора: пользователь отказался продолжать");
                return;
            }

            AppLogger.Write($"[App] Продолжение незавершённой установки набора: {pending.AppIds.Count}");
            // Тем же путём, что и установка по заданию, но с окном и обычными вопросами.
            _ = RunUnattendedAsync(main, new UnattendedRequest
            {
                AppIds = pending.AppIds,
                InstallDrive = pending.InstallDrive
            });
        }

        internal static string DescribeIds(System.Collections.Generic.IReadOnlyList<string> ids) =>
            ids.Count <= 5
                ? string.Join(", ", ids)
                : string.Join(", ", System.Linq.Enumerable.Take(ids, 5)) + $" и ещё {ids.Count - 5}";

        /// <summary>
        /// Задание <c>--update-apps</c>: то же обновление, что делает автообновление по
        /// расписанию, но сейчас и независимо от того, включено ли оно в настройках.
        /// </summary>
        private static async System.Threading.Tasks.Task<UnattendedReport> RunUpdateAppsAsync()
        {
            var report = new UnattendedReport { StartedUtc = DateTime.UtcNow.ToString("o") };
            var result = await AutoUpdateService.RunAsync(
                ProfileService.Current.AutoUpdateExcluded, AppLogger.Write, CancellationToken.None);

            report.Installed.AddRange(result.Updated);
            foreach (var (id, reason) in result.Failed)
                report.Failed.Add(new UnattendedFailure { Id = id, Reason = reason });
            report.Unavailable.AddRange(result.Excluded);
            report.Message = result.Describe();
            report.ExitCode = result.WingetUnavailable ? UnattendedExitCode.NothingToInstall
                : result.Failed.Count > 0 ? UnattendedExitCode.PartialFailure
                : UnattendedExitCode.Success;
            report.FinishedUtc = DateTime.UtcNow.ToString("o");
            AppLogger.Write($"🤖 Обновление программ по заданию: {report.Message}");

            if (!result.WingetUnavailable)
            {
                ProfileService.Current.AutoUpdateLastRunUtc = DateTime.UtcNow;
                ProfileService.Save();
            }
            return report;
        }

        /// <summary>
        /// Если прошлый сеанс завершился сбоем — спрашивает пользователя, отправить
        /// ли отчёт разработчику. Отправка выполняется только при явном «Да»;
        /// при «Нет» отчёт удаляется и повторно не предлагается. Если согласие
        /// уже было дано ранее (отправка сорвалась из-за сети) — отправляем без
        /// повторного вопроса.
        /// </summary>
        private static void AskAndSendPendingCrashReport()
        {
            var report = CrashReportService.Read();
            if (report == null || report.Reported) return;

            // Параноидальный режим обещает блокировать отправку краш-отчётов.
            // Вопрос здесь был бы нечестным: TrySendPendingAsync в этом режиме молча
            // ничего не отправляет, а файл отчёта остаётся на диске — и лаунчер при
            // следующем запуске предложит опубликовать его в ПУБЛИЧНОМ issue на GitHub,
            // ровно то, чего пользователь этим режимом и не хочет. Поэтому не
            // спрашиваем и сразу удаляем отчёт: отправлять его всё равно некуда.
            if (ProfileService.Current.ParanoidMode)
            {
                CrashReportService.DeletePending();
                AppLogger.Write("[App] Отчёт о сбое удалён без отправки: включён параноидальный режим");
                return;
            }

            if (!report.SendApproved)
            {
                var answer = MessageBox.Show(
                    "Обнаружен отчёт о сбое предыдущего запуска.\n\n" +
                    "Отправить разработчику для диагностики?\n" +
                    "Отчёт не содержит личных данных.",
                    "Ven4Tools — отчёт о сбое",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                {
                    CrashReportService.DeletePending();
                    return;
                }

                CrashReportService.MarkSendApproved();
            }

            // Отправка — fire-and-forget, старт приложения не блокирует
            _ = CrashReportService.TrySendPendingAsync();
        }

        /// <summary>
        /// Освобождает мьютекс единственного экземпляра до завершения процесса.
        /// Нужно вызывать перед запуском повышенной копии клиента (RestartAsAdmin):
        /// иначе повышенная копия может увидеть мьютекс ещё занятым и выйти как
        /// «уже запущено», и не останется ни одного рабочего экземпляра.
        /// Идемпотентно.
        /// </summary>
        public static void ReleaseSingleInstanceMutex()
        {
            if (_instanceMutex != null)
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
                _instanceMutex = null;
            }
        }

        /// <summary>
        /// Восстанавливает мьютекс единственного экземпляра, если повышенная копия
        /// так и не стартовала (пользователь отклонил UAC) — чтобы состояние
        /// единственного экземпляра оставалось согласованным.
        ///
        /// Признак владения (createdNew) обязателен ровно так же, как в OnStartup:
        /// пока висит запрос UAC, мьютекс отпущен, и за эти секунды другой запуск
        /// клиента успевает его занять. Тогда конструктор владения НЕ даёт, а поле
        /// всё равно заполнялось — и OnExit вызывал ReleaseMutex на невладеемом
        /// мьютексе, то есть ApplicationException прямо при завершении приложения.
        /// Инвариант поля: непустое ⇒ мьютекс принадлежит этому процессу.
        /// </summary>
        public static void ReacquireSingleInstanceMutex()
        {
            if (_instanceMutex != null) return;

            var mutex = new Mutex(true, "Ven4Tools.Client.SingleInstance", out bool createdNew);
            if (createdNew)
            {
                _instanceMutex = mutex;
                return;
            }

            // Мьютекс уже занят другим экземпляром — владения нет, держать нечего.
            mutex.Dispose();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _heartbeat?.Dispose();
            _updateBgService?.Dispose();
            _windowsUpdateBgService?.Dispose();
            _clientControlServer?.Dispose();
            _clientControlServer = null;
            if (_instanceMutex != null)
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
                _instanceMutex = null;
            }
            base.OnExit(e);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown");

            try { CrashReportService.Write(ex); } catch { }

            try
            {
                Dispatcher.Invoke(() => Shutdown(-1));
            }
            catch { }
        }

        private void Current_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Exception ex = e.Exception;

            try { CrashReportService.Write(ex); } catch { }

            // Не глушим исключение молча: показываем сообщение и завершаем приложение,
            // чтобы не остаться в неопределённом состоянии после фатальной UI-ошибки.
            try
            {
                MessageBox.Show(
                    "Произошла непредвиденная ошибка, приложение будет закрыто.\n\n" + ex.Message,
                    "Ven4Tools — ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { }

            // Помечаем обработанным, чтобы вместо системного «crash»-диалога
            // выполнить контролируемое завершение.
            e.Handled = true;
            try { Shutdown(-1); } catch { }
        }
    }
}
