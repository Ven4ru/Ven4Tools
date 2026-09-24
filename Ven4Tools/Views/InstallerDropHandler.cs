using System;
using System.IO;
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
            var files = GetDroppedInstallers(e, checkExists: true);
            if (files.Length == 0) return;

            // Диалоги показываем уже после выхода из обработчика Drop: модальное окно
            // прямо внутри него держит OLE-цикл перетаскивания источника (Проводник
            // «замерзает», пока пользователь не закроет диалог).
            _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var file in files)
                {
                    var dlg = new LocalInstallerDialog(file) { Owner = _owner };
                    if (dlg.ShowDialog() == true && dlg.Result != null)
                    {
                        AppLogger.Write($"📦 Добавлен локальный установщик: {dlg.Result.DisplayName}");
                        // Передаём во вкладку каталога — в механизм пользовательских приложений
                        _installerAccepted(dlg.Result);
                    }
                }
            }));
        }

        // DragOver приходит на каждое движение мыши — без обращения к диску (сетевые пути).
        private static bool IsExeOrMsi(DragEventArgs e) => GetDroppedInstallers(e, checkExists: false).Length > 0;

        // GetData может вернуть null (источник объявил FileDrop, но данных не отдал) —
        // прямое приведение (string[]) роняло бы обработчик DragEnter/DragOver.
        // Каталог с именем «setup.exe» установщиком не является — только существующие файлы.
        private static string[] GetDroppedInstallers(DragEventArgs e, bool checkExists)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return Array.Empty<string>();
                return files
                    .Where(f => !string.IsNullOrEmpty(f)
                                && (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                    || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                                && (!checkExists || File.Exists(f)))
                    .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
