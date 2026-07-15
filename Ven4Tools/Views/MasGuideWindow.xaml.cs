using System;
using System.Diagnostics;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    // Окно-помощник: пошаговая инструкция по управлению лицензией (Windows/Office)
    public partial class MasGuideWindow : Window
    {
        public MasGuideWindow(string product)
        {
            InitializeComponent();
            txtWindowTitle.Text = $"🔑 Управление лицензией {product}";
            txtStep1Body.Text = product == "Office"
                ? "На сайте massgrave.dev найдите раздел активации Office и скопируйте команду (обычно начинается с irm или iwr)."
                : "На сайте massgrave.dev найдите раздел активации Windows и скопируйте команду (обычно начинается с irm или iwr).";

            btnOpenTerminal.Click += (_, _) => OpenTerminal();
            btnClose.Click        += (_, _) => Close();
        }

        private void OpenTerminal()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Ven4Tools.Services.TrustedExecutablePaths.PowerShellExe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[MasGuide] Не удалось открыть PowerShell: {ex.Message}");
                MessageBox.Show("Не удалось открыть PowerShell. Откройте его вручную через меню «Пуск».",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
