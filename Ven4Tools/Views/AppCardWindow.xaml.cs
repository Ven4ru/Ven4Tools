using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
