using System;
using System.Windows;

namespace Ven4Admin
{
    public partial class AppEditWindow : Window
    {
        public CatalogItem? Result { get; private set; }

        private static readonly string[] Categories =
        {
            "Браузеры", "Офис", "Графика", "Разработка",
            "Мессенджеры", "Мультимедиа", "Системные",
            "Игровые сервисы", "Драйверпаки", "Другое"
        };

        public AppEditWindow(CatalogItem? existing)
        {
            InitializeComponent();

            foreach (var cat in Categories)
                cmbCategory.Items.Add(cat);
            cmbCategory.SelectedIndex = 0;

            if (existing != null)
            {
                Title = $"Редактирование: {existing.Name}";
                txtName.Text        = existing.Name;
                cmbCategory.SelectedItem = existing.Category;
                if (cmbCategory.SelectedIndex < 0) cmbCategory.SelectedIndex = 0;
                txtWingetId.Text    = existing.WingetId;
                txtUrl.Text         = existing.DownloadUrl;
                txtVersion.Text     = existing.Version;
                txtSize.Text        = existing.Size;
                txtIconUrl.Text     = existing.IconUrl;
                txtDescription.Text = existing.Description;
                chkOfficial.IsChecked  = existing.Official;
                chkRuBlocked.IsChecked = existing.RuBlocked;
                chkSkipHash.IsChecked  = existing.SkipHash;
            }
            else
            {
                Title = "Добавить приложение";
                chkOfficial.IsChecked = true;
            }

            btnOk.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(txtName.Text))
                {
                    MessageBox.Show("Укажите название приложения.", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtName.Focus();
                    return;
                }

                Result = new CatalogItem
                {
                    Id          = existing?.Id ?? Guid.NewGuid().ToString(),
                    Name        = txtName.Text.Trim(),
                    Category    = cmbCategory.SelectedItem?.ToString() ?? "Другое",
                    WingetId    = txtWingetId.Text.Trim(),
                    DownloadUrl = txtUrl.Text.Trim(),
                    Version     = txtVersion.Text.Trim(),
                    Size        = txtSize.Text.Trim(),
                    IconUrl     = txtIconUrl.Text.Trim(),
                    Description = txtDescription.Text.Trim(),
                    Official    = chkOfficial.IsChecked  == true,
                    RuBlocked   = chkRuBlocked.IsChecked == true,
                    SkipHash    = chkSkipHash.IsChecked  == true,
                    Sha256      = existing?.Sha256,
                };
                DialogResult = true;
                Close();
            };

            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        }
    }
}
