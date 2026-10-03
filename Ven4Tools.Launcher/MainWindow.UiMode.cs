using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Два вида окна лаунчера — новый и прежний — и переключение между ними.
    ///
    /// Прежний: боковая панель с восемью кнопками и рабочая область. Новый: боковой
    /// панели нет, главное действие и версии стоят в карточке клиента, редкие действия
    /// («Найти клиент», «Удалить клиент», «Закрыть лаунчер») — в нижней полосе, журнал
    /// свёрнут. Кнопки и подписи в обоих видах — одни и те же элементы: они
    /// переставляются между панелями, поэтому обработчики, имена для автоматизации и
    /// вся логика состояний работают одинаково.
    /// </summary>
    public partial class MainWindow
    {
        private const string UiModeModern = "modern";
        private const string UiModeClassic = "classic";

        // Записывается в launcher_settings.json.
        private string _uiMode = UiModeModern;
        private bool _uiModern;
        private bool _logCollapsedOnce;
        private LaunchButtonState? _launchState;

        // Свойства, которые новый вид меняет у переставленных элементов. Прежние значения
        // запоминаются и возвращаются как были — заданные в разметке или пришедшие из стиля.
        private static readonly DependencyProperty[] TunedProperties =
        {
            MarginProperty, HeightProperty, MinWidthProperty, FontSizeProperty,
            HorizontalAlignmentProperty, VerticalAlignmentProperty, ContentControl.ContentTemplateProperty
        };

        private sealed record ClassicPlacement(Panel Parent, int Index, object[] LocalValues);

        private Dictionary<FrameworkElement, ClassicPlacement>? _classicPlacement;

        private FrameworkElement[] MovableElements => new FrameworkElement[]
        {
            brandBlock,
            btnLaunchApp, btnInstallFromFile, btnCheckUpdates, btnChangelog,
            btnFindClient, txtInstalledVersion, txtClientVersion, txtVersionInfo,
            btnOpenSettings, btnInstallUpdate, btnInstallMissing,
            btnDeleteClient, btnExit, btnUiModeSwitch
        };

        private void ApplyUiMode()
        {
            _uiModern = !string.Equals(_uiMode, UiModeClassic, StringComparison.OrdinalIgnoreCase);

            _classicPlacement ??= MovableElements.ToDictionary(
                element => element,
                element =>
                {
                    var parent = (Panel)element.Parent;
                    return new ClassicPlacement(parent, parent.Children.IndexOf(element),
                        TunedProperties.Select(element.ReadLocalValue).ToArray());
                });

            foreach (var element in MovableElements)
                (element.Parent as Panel)?.Children.Remove(element);

            if (_uiModern) PlaceModern();
            else PlaceClassic();

            Visibility modern = _uiModern ? Visibility.Visible : Visibility.Collapsed;
            Visibility classic = _uiModern ? Visibility.Collapsed : Visibility.Visible;
            barTop.Visibility = modern;
            barBottom.Visibility = modern;
            slotHeroVersions.Visibility = modern;
            slotHeroActions.Visibility = modern;
            slotAlerts.Visibility = modern;
            sidebar.Visibility = classic;
            pageHeader.Visibility = classic;
            txtHeroDescription.Visibility = classic;
            colSidebar.Width = new GridLength(_uiModern ? 0 : 232);
            Grid.SetColumn(gridMain, _uiModern ? 0 : 1);
            Grid.SetColumnSpan(gridMain, _uiModern ? 2 : 1);
            gridContent.Margin = _uiModern ? new Thickness(28, 20, 28, 16) : new Thickness(28, 22, 28, 24);

            // Папка установки: в широкой карточке нового вида растянутая на всю ширину
            // панель с MaxWidth встала бы по центру.
            btnSelectFolder.HorizontalAlignment = _uiModern ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            btnSelectFolder.MinWidth = _uiModern ? 520 : 0;

            // Журнал в новом виде свёрнут — один раз, при первом показе: дальше его
            // положением распоряжается пользователь.
            if (_uiModern && !_logCollapsedOnce)
            {
                _logCollapsedOnce = true;
                logExpander.IsExpanded = false;
            }

            btnUiModeSwitch.Content = _uiModern ? "Старый интерфейс" : "Новый интерфейс";
            UpdateHeroTitle();
            if (_launchState is { } state) SetLaunchButtonState(state);
        }

        private void PlaceModern()
        {
            var padded = (DataTemplate)FindResource("PaddedButtonText");

            slotBrand.Children.Add(brandBlock);

            AddTo(slotTopActions, btnCheckUpdates, new Thickness(0, 0, 8, 0), 34, padded);
            AddTo(slotTopActions, btnOpenSettings, new Thickness(0), 34, padded);

            foreach (var text in new[] { txtInstalledVersion, txtClientVersion, txtVersionInfo })
            {
                slotHeroVersions.Children.Add(text);
                text.Margin = new Thickness(0, 0, 18, 0);
                text.VerticalAlignment = VerticalAlignment.Center;
                text.FontSize = 12;
            }

            AddTo(slotHeroActions, btnLaunchApp, new Thickness(0, 0, 10, 0), 44, padded);
            btnLaunchApp.MinWidth = 230;
            AddTo(slotHeroActions, btnChangelog, new Thickness(0, 0, 8, 0), 44, padded);
            AddTo(slotHeroActions, btnInstallFromFile, new Thickness(0), 44, padded);

            AddTo(slotAlerts, btnInstallUpdate, new Thickness(0, 0, 8, 0), 38, padded);
            AddTo(slotAlerts, btnInstallMissing, new Thickness(0), 38, padded);

            slotBottomLeft.Children.Add(btnUiModeSwitch);
            AddTo(slotBottomActions, btnFindClient, new Thickness(0, 0, 8, 0), 32, padded);
            AddTo(slotBottomActions, btnDeleteClient, new Thickness(0, 0, 8, 0), 32, padded);
            AddTo(slotBottomActions, btnExit, new Thickness(0), 32, padded);
        }

        private static void AddTo(Panel slot, Button button, Thickness margin, double height, DataTemplate contentTemplate)
        {
            slot.Children.Add(button);
            button.Margin = margin;
            button.Height = height;
            button.ContentTemplate = contentTemplate;
        }

        private void PlaceClassic()
        {
            // По возрастанию исходного индекса: неподвижные соседи (подписи, разделитель)
            // остались на месте, поэтому каждый элемент встаёт ровно туда, где был.
            foreach (var (element, placement) in _classicPlacement!.OrderBy(pair => pair.Value.Index))
            {
                placement.Parent.Children.Insert(Math.Min(placement.Index, placement.Parent.Children.Count), element);
                for (int i = 0; i < TunedProperties.Length; i++)
                {
                    object local = placement.LocalValues[i];
                    if (local == DependencyProperty.UnsetValue) element.ClearValue(TunedProperties[i]);
                    else element.SetValue(TunedProperties[i], local);
                }
            }
        }

        /// <summary>
        /// Заголовок карточки клиента. В прежнем виде он постоянный; в новом говорит,
        /// что с клиентом сейчас, — рядом стоит кнопка, которая на это отвечает.
        /// </summary>
        private void UpdateHeroTitle()
        {
            txtHeroTitle.Text = !_uiModern ? "Готов к установке" : _launchState switch
            {
                LaunchButtonState.Launch => "Клиент установлен",
                LaunchButtonState.Download => "Клиент не установлен",
                LaunchButtonState.Update => "Доступно обновление клиента",
                _ => "Готов к установке"
            };
        }

        /// <summary>
        /// Цвет главной кнопки в новом виде: зелёный — фирменный цвет действия «вперёд»,
        /// янтарный оставлен обновлению. Тёмная подпись на синем и оранжевом прежнего
        /// вида читалась хуже.
        /// </summary>
        private Brush ModernLaunchBrush(LaunchButtonState state) =>
            state == LaunchButtonState.Update ? UpdateBrush : (Brush)FindResource("BrandGreen");

        private void BtnUiModeSwitch_Click(object sender, RoutedEventArgs e)
        {
            _uiMode = _uiModern ? UiModeClassic : UiModeModern;
            SaveSettings();
            ApplyUiMode();
            AddLog(_uiModern ? "🎨 Включён новый интерфейс" : "🎨 Включён прежний интерфейс");
        }
    }
}
