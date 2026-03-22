using System;
using System.Windows;
using System.Windows.Threading;

namespace Ven4Tools
{
    public partial class App : Application
    {
        public App()
        {
            // Глобальный обработчик непойманных исключений
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = (Exception)e.ExceptionObject;
            string errorMessage = $"Критическая ошибка: {ex.Message}\n\nСтек:\n{ex.StackTrace}";
            
            // Пытаемся показать сообщение
            try
            {
                MessageBox.Show(errorMessage, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            
            // Записываем в файл
            try
            {
                string logPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ven4Tools", "crash.log");
                
                System.IO.File.WriteAllText(logPath, 
                    $"{DateTime.Now} - {ex.Message}\n{ex.StackTrace}");
            }
            catch { }
        }

        private void Current_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            CurrentDomain_UnhandledException(sender, 
                new UnhandledExceptionEventArgs(e.Exception, false));
        }
    }
}