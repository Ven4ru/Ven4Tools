using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Окно объяснения: антивирус/нет доступа (Л3) и блокировка запуска политикой (Л5).
    /// Показывает текст, SHA256 файла (если известен) и ссылки только на разрешённые
    /// сайты (<see cref="SafeLinkOpener"/>).
    /// </summary>
    public partial class TrustNoticeWindow : Window
    {
        internal TrustNoticeWindow(string title, string body, string? sha256, string? hashLabel,
            IReadOnlyList<AdviceLink>? links)
        {
            InitializeComponent();
            Title = $"{title} — Ven4Tools Launcher";
            txtHeader.Text = title;
            txtBody.Text = body;

            if (!string.IsNullOrEmpty(sha256))
            {
                txtHash.Text = sha256;
                if (!string.IsNullOrEmpty(hashLabel)) txtHashLabel.Text = hashLabel;
                pnlHash.Visibility = Visibility.Visible;
            }

            if (links is { Count: > 0 })
            {
                lstLinks.ItemsSource = links;
                pnlLinks.Visibility = Visibility.Visible;
            }
        }

        private void BtnCopyHash_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(txtHash.Text); }
            catch (System.Exception ex) { LauncherLog.Write($"[Буфер обмена] Не удалось скопировать: {ex.Message}"); }
        }

        private void BtnLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string url }) SafeLinkOpener.Open(url);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
