using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Полоса закреплённых приложений над рабочей областью: строит карточки пинов,
    /// снимает закрепление и ставит приложение из пина. Список идентификаторов
    /// хранит <see cref="PinnedAppsService"/> — здесь только представление и установка.
    /// Главному окну достаточно создать контроллер и вызывать <see cref="Refresh"/>.
    /// </summary>
    public sealed class PinsStripController
    {
        private readonly UIElement _panel;
        private readonly Panel _cards;
        private readonly FrameworkElement _resourceHost;
        private readonly Func<string> _installDriveProvider;

        /// <param name="panel">Контейнер всей полосы — скрывается, когда пинов нет.</param>
        /// <param name="cards">Панель, в которую складываются карточки пинов.</param>
        /// <param name="resourceHost">Элемент, через который берутся кисти темы (обычно окно).</param>
        /// <param name="installDriveProvider">Диск установки, выбранный в каталоге.</param>
        public PinsStripController(UIElement panel, Panel cards, FrameworkElement resourceHost,
                                   Func<string> installDriveProvider)
        {
            _panel = panel;
            _cards = cards;
            _resourceHost = resourceHost;
            _installDriveProvider = installDriveProvider;
        }

        public void Refresh()
        {
            var pins = PinnedAppsService.Pinned;
            if (pins.Count == 0) { _panel.Visibility = Visibility.Collapsed; return; }

            _panel.Visibility = Visibility.Visible;
            _cards.Children.Clear();
            var catalog = CatalogLoaderService.State.Catalog;

            foreach (var id in pins)
            {
                var app = catalog?.Apps.FirstOrDefault(a => a.Id == id);
                string name = app?.Name ?? id;

                var card = new Border
                {
                    Background    = (Brush)_resourceHost.FindResource("CardBackground"),
                    CornerRadius  = new CornerRadius(8),
                    Padding       = new Thickness(8, 4, 4, 4),
                    Margin        = new Thickness(0, 0, 6, 0),
                    Cursor        = System.Windows.Input.Cursors.Hand
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = name.Length > 16 ? name.Substring(0, 16) + "…" : name,
                    Foreground = (Brush)_resourceHost.FindResource("TextPrimary"),
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                var installBtn = new Button
                {
                    Content = "▶", Width = 22, Height = 22, FontSize = 9,
                    Tag = id, Padding = new Thickness(0),
                    ToolTip = $"Установит закреплённое приложение «{name}»."
                };
                installBtn.Click += PinInstallBtn_Click;
                var unpinBtn = new Button
                {
                    Content = "×", Width = 18, Height = 18, FontSize = 10,
                    Tag = id, Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = (Brush)_resourceHost.FindResource("TextSecondary"),
                    ToolTip = "Уберёт приложение из панели закреплённых. Само приложение останется на компьютере."
                };
                unpinBtn.Click += PinUnpinBtn_Click;
                row.Children.Add(installBtn);
                row.Children.Add(unpinBtn);
                card.Child = row;
                _cards.Children.Add(card);
            }
        }

        private async void PinInstallBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not string id) return;
            if (UiGuards.WarnIfInstallBusy()) return;

            var catalog = CatalogLoaderService.State.Catalog;
            var catalogApp = catalog?.Apps.FirstOrDefault(a => a.Id == id);
            if (catalogApp == null) { AppLogger.Write($"❌ Приложение {id} не найдено в каталоге"); return; }

            var btn = sender as Button;

            AppLogger.Write($"📌 Установка из пина: {catalogApp.Name}...");
            var appInfo = new AppInfo
            {
                Id = catalogApp.Id, DisplayName = catalogApp.Name,
                AlternativeId = catalogApp.WingetId,
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                    ? new List<string> { catalogApp.DownloadUrl } : new(),
                ChocoId = catalogApp.ChocoId,
                // SHA256 обязателен для установки из пина по прямой ссылке.
                Sha256 = catalogApp.Sha256
            };
            // Переопределение тихого флага (напр. AutoHotkey v2: "/silent" вместо "/S") —
            // без этого установка из пина теряет override и падает на дефолтном "/S"
            // (тот же фикс, что уже применён к переустановке из истории — HistoryTab).
            if (!string.IsNullOrEmpty(catalogApp.SilentArgs))
                appInfo.SilentArgs = catalogApp.SilentArgs;
            var prog = new Progress<AppInstallProgress>(p => AppLogger.Write($"  {p.Status}"));

            // Общий семафор: не даём пину запустить установку параллельно с каталогом/историей.
            if (btn != null) btn.IsEnabled = false;
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                using var installer = new InstallationService();
                using var cts = new CancellationTokenSource();
                string installDrive = _installDriveProvider();
                var r = await installer.InstallAppAsync(appInfo, new[] { "winget", "msstore" }, cts.Token, prog, installDrive, null, UiGuards.ConfirmPackageManagerInstallAsync);
                AppLogger.Write(r.Success ? $"✅ {catalogApp.Name}" : $"❌ {r.Message}");
            }
            catch (OperationCanceledException)
            {
                // InstallAppAsync гасит обычные ошибки и возвращает (false, сообщение),
                // но отмену пробрасывает наружу намеренно. Сюда же попадает таймаут
                // HttpClient при прямой загрузке (TaskCanceledException). Без этого
                // блока исключение вылетало бы из async void-обработчика и роняло
                // приложение целиком — как уже сделано в каталоге, карточке, Office
                // и вкладке «Установленные».
                AppLogger.Write($"⏹️ Установка {catalogApp.Name} прервана");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка установки {catalogApp.Name}: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private void PinUnpinBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not string id) return;
            PinnedAppsService.Unpin(id);
            Refresh();
        }
    }
}
