using System.Windows;

namespace Ven4Tools.Localization
{
    /// <summary>
    /// Системное окно сообщения с переводом текста и заголовка.
    ///
    /// Окно сообщения рисует сама Windows, мимо элементов WPF, поэтому перевод, который
    /// подставляется при показе элементов (<see cref="UiTranslator"/>), до него не
    /// доходит. В обоих проектах имя <c>MessageBox</c> указывает на этот класс
    /// (<c>global using</c>), так что вызовы в коде остались прежними.
    /// </summary>
    public static class MessageBoxL
    {
        private static string T(string? text) => UiTranslator.Tr(text);

        public static MessageBoxResult Show(string? messageBoxText) =>
            System.Windows.MessageBox.Show(T(messageBoxText));

        public static MessageBoxResult Show(string? messageBoxText, string? caption) =>
            System.Windows.MessageBox.Show(T(messageBoxText), T(caption));

        public static MessageBoxResult Show(string? messageBoxText, string? caption, MessageBoxButton button) =>
            System.Windows.MessageBox.Show(T(messageBoxText), T(caption), button);

        public static MessageBoxResult Show(string? messageBoxText, string? caption, MessageBoxButton button, MessageBoxImage icon) =>
            System.Windows.MessageBox.Show(T(messageBoxText), T(caption), button, icon);

        public static MessageBoxResult Show(string? messageBoxText, string? caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
            System.Windows.MessageBox.Show(T(messageBoxText), T(caption), button, icon, defaultResult);

        public static MessageBoxResult Show(string? messageBoxText, string? caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult, MessageBoxOptions options) =>
            System.Windows.MessageBox.Show(T(messageBoxText), T(caption), button, icon, defaultResult, options);

        public static MessageBoxResult Show(Window owner, string? messageBoxText) =>
            System.Windows.MessageBox.Show(owner, T(messageBoxText));

        public static MessageBoxResult Show(Window owner, string? messageBoxText, string? caption) =>
            System.Windows.MessageBox.Show(owner, T(messageBoxText), T(caption));

        public static MessageBoxResult Show(Window owner, string? messageBoxText, string? caption, MessageBoxButton button) =>
            System.Windows.MessageBox.Show(owner, T(messageBoxText), T(caption), button);

        public static MessageBoxResult Show(Window owner, string? messageBoxText, string? caption, MessageBoxButton button, MessageBoxImage icon) =>
            System.Windows.MessageBox.Show(owner, T(messageBoxText), T(caption), button, icon);

        public static MessageBoxResult Show(Window owner, string? messageBoxText, string? caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
            System.Windows.MessageBox.Show(owner, T(messageBoxText), T(caption), button, icon, defaultResult);
    }
}
