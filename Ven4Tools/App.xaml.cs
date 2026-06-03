using System;
using System.Windows;
using System.Windows.Threading;
using Ven4Tools.Services;

namespace Ven4Tools
{
    public partial class App : Application
    {
        private static HeartbeatService? _heartbeat;

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            LocalizationService.Init();
            ThemeService.Apply(ProfileService.Current.Theme);
            _heartbeat = new HeartbeatService();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _heartbeat?.Dispose();
            base.OnExit(e);
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown");

            try { CrashReportService.Write(ex); } catch { }

            try
            {
                Shutdown(-1);
            }
            catch { }
        }

        private void Current_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Exception ex = e.Exception;

            try { CrashReportService.Write(ex); } catch { }
        }
    }
}