using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views
{
    public partial class AppCardWindow : Window
    {
        public AppCardWindow(AppCardViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += Close;
        }

        // Esc закрывает модальную карточку — стандартное ожидание для модалок,
        // отдельная ViewModel-команда для этого избыточна.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Write($"❌ Ошибка: {ex.Message}"); }
            e.Handled = true;
        }
    }
}
