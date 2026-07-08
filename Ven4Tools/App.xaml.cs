using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ven4Tools.Services;
using Ven4Tools.Views;

namespace Ven4Tools
{
    public partial class App : Application
    {
        private static HeartbeatService? _heartbeat;
        private static UpdateBackgroundService? _updateBgService;
        private static WindowsUpdateBackgroundService? _windowsUpdateBgService;
        private static Mutex? _instanceMutex;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Единственный экземпляр клиента: два процесса гонялись бы за файлами
            // (profile.json, apps.json) и могли запустить параллельные установки.
            _instanceMutex = new Mutex(true, "Ven4Tools.Client.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Приложение Ven4Tools уже запущено.",
                    "Уже запущено",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
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
                // Эти вызовы не должны валить старт — каждый best-effort.
                try { LocalizationService.Init(); } catch { }
                try { ThemeService.Apply(ProfileService.Current.Theme); } catch { }
                try { _heartbeat = new HeartbeatService(); } catch { }
                // Краш-репорт прошлого сеанса отправляется только с явного согласия пользователя
                try { AskAndSendPendingCrashReport(); } catch { }
                // Отправка отложенного отзыва — тоже fire-and-forget
                try { _ = FeedbackService.TrySendPendingAsync(); } catch { }

                try
                {
                    splash = new SplashWindow();
                    splash.Show();
                    await splash.RunPreloadAsync();
                }
                catch { /* splash/preload — необязательная фаза, продолжаем старт */ }

                var main = new MainWindow();
                main.Show();

                // Фоновые уведомления об обновлениях/новых приложениях — после показа
                // окна, чтобы трей-иконка успела зарегистрироваться. Старт не блокирует.
                try
                {
                    _updateBgService = new UpdateBackgroundService();
                    _updateBgService.Start();
                }
                catch { }

                try
                {
                    _windowsUpdateBgService = new WindowsUpdateBackgroundService();
                    _windowsUpdateBgService.Start();
                }
                catch { }
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

        protected override void OnExit(ExitEventArgs e)
        {
            _heartbeat?.Dispose();
            _updateBgService?.Dispose();
            _windowsUpdateBgService?.Dispose();
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
