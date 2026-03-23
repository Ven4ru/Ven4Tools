using System.Windows;

namespace Ven4Tools.Views
{
    public partial class ConsentDialog : Window
    {
        public bool AllowStats { get; private set; }
        
        public ConsentDialog()
        {
            InitializeComponent();
        }
        
        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            AllowStats = true;
            DialogResult = true;
            Close();
        }
        
        private void Decline_Click(object sender, RoutedEventArgs e)
        {
            AllowStats = false;
            DialogResult = true;
            Close();
        }
    }
}