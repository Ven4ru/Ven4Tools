using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Маскот в боковой панели: подбирает картинку под открытую вкладку и прячет
    /// её вне «веб»-темы. Вынесено из главного окна — окну достаточно сообщить имя
    /// текущей вкладки.
    /// </summary>
    public sealed class MascotController
    {
        private readonly Image _image;

        public MascotController(Image image) => _image = image;

        public void Show(string tabName)
        {
            if (ProfileService.Current.Theme != "web")
            {
                _image.Visibility = Visibility.Collapsed;
                return;
            }
            // Своего маскота есть не у каждой вкладки. Часть переходов передаёт
            // сюда нейтральное "system" явно, но вкладки «Установленные»,
            // «Очистка» и «История» передают собственное имя, файла для
            // которого в Resources\Mascots нет — там маскот просто молча
            // пропадал, хотя на соседних вкладках оставался на месте. Общий
            // откат на "system" убирает это расхождение и заодно избавляет
            // будущие вкладки от необходимости помнить про такой случай.
            if (!TryShow(tabName) && !TryShow("system"))
            {
                _image.Visibility = Visibility.Collapsed;
            }
        }

        private bool TryShow(string tabName)
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Resources/Mascots/{tabName}.png");
                _image.Source = new BitmapImage(uri);
                _image.Visibility = Visibility.Visible;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
