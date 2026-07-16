using System.Windows;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Диалог показа лицензионных соглашений обновлений Windows с прокруткой.
    /// Заменяет склеенный в один MessageBox текст EULA, который обрезался по высоте
    /// экрана без возможности прокрутки. Логика принятия/отклонения не меняется:
    /// DialogResult == true эквивалентен прежнему «Да».
    /// </summary>
    public partial class EulaConfirmWindow : Window
    {
        public EulaConfirmWindow(string header, string eulaText)
        {
            InitializeComponent();
            txtHeader.Text = header;
            txtEula.Text = eulaText;
        }

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnDecline_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
