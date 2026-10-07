using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Окно «Набор из установленного»: список программ каталога, стоящих на этом
    /// компьютере, их код набора и сохранение файла ответа для тихой установки.
    /// Отметки в каталоге окно не трогает — оно только показывает и отдаёт набор.
    /// </summary>
    public partial class InstalledSetWindow : Window
    {
        private readonly InstalledSetBuilder.InstalledSet _set;

        public InstalledSetWindow(InstalledSetBuilder.InstalledSet set)
        {
            InitializeComponent();
            _set = set;

            txtInstalledSetTitle.Text = $"Установлено из каталога: {set.Names.Count}";
            lstInstalledSet.ItemsSource = set.Names;
            txtInstalledSetCode.Text = set.Code;
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_set.Code);
                ShowHint("Код набора скопирован.", error: false);
            }
            catch (Exception ex)
            {
                // Буфер обмена бывает занят другим процессом.
                ShowHint($"Не удалось скопировать: {ex.Message}", error: true);
            }
        }

        private void SaveAnswerFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = Tr("Файл ответа для тихой установки"),
                Filter = Tr("Файл ответа Ven4Tools (*.json)|*.json"),
                FileName = Tr("ven4tools-набор.json"),
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, InstalledSetBuilder.BuildAnswerFile(_set.AppIds));
                AppLogger.Write($"💾 Файл ответа сохранён: программ в наборе — {_set.AppIds.Count}");
                ShowHint($"Сохранено. Установка без вопросов: Ven4Tools.exe --answer-file \"{dialog.FileName}\"", error: false);
            }
            catch (Exception ex)
            {
                ShowHint($"Не удалось сохранить файл: {ex.Message}", error: true);
            }
        }

        private void ShowHint(string text, bool error)
        {
            txtInstalledSetHint.Text = text;
            txtInstalledSetHint.Foreground = (System.Windows.Media.Brush)FindResource(error ? "StatusDanger" : "AccentColor");
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
