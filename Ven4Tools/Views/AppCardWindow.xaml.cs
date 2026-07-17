using System.Windows;
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

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
