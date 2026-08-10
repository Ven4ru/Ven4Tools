using System;
using System.Linq;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Перетаскивание .exe/.msi в рабочую область: подсветка зоны сброса и диалог
    /// добавления локального установщика. Главное окно только перенаправляет сюда
    /// XAML-события drag&amp;drop, а принятое приложение получает через обратный вызов.
    /// </summary>
    public sealed class InstallerDropHandler
    {
        private readonly Window _owner;
        private readonly UIElement _overlay;
        private readonly Action<AppInfo> _installerAccepted;

        public InstallerDropHandler(Window owner, UIElement overlay, Action<AppInfo> installerAccepted)
        {
            _owner = owner;
            _overlay = overlay;
            _installerAccepted = installerAccepted;
        }

        public void DragEnter(DragEventArgs e)
        {
            if (IsExeOrMsi(e))
            {
                e.Effects = DragDropEffects.Copy;
                _overlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        public void DragOver(DragEventArgs e)
        {
            e.Effects = IsExeOrMsi(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        public void DragLeave(DragEventArgs e)
        {
            _overlay.Visibility = Visibility.Collapsed;
        }

        public void Drop(DragEventArgs e)
        {
            _overlay.Visibility = Visibility.Collapsed;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;

                var dlg = new LocalInstallerDialog(file) { Owner = _owner };
                if (dlg.ShowDialog() == true && dlg.Result != null)
                {
                    AppLogger.Write($"📦 Добавлен локальный установщик: {dlg.Result.DisplayName}");
                    // Передаём во вкладку каталога — в механизм пользовательских приложений
                    _installerAccepted(dlg.Result);
                }
            }
        }

        private static bool IsExeOrMsi(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            return files.Any(f =>
                f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
        }
    }
}
