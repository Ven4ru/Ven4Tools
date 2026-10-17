using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Затухание по краям прокручиваемого списка: строки плавно уходят в прозрачность у
    /// того края, за которым есть ещё содержимое. Полоса прокрутки видна только при
    /// наведении (стиль ScrollBar в App.xaml), и без затухания у короткого списка не
    /// понять, что ниже есть ещё строки.
    /// Ставится на ScrollViewer или на элемент со своим ScrollViewer внутри (ListBox).
    /// Маска ложится только на содержимое — сама полоса прокрутки не тускнеет.
    /// </summary>
    public static class ScrollFade
    {
        // Высота затухания в пикселях; у очень низкого списка — не больше трети высоты.
        private const double FadeSize = 20;

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(ScrollFade), new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe || e.NewValue is not true) return;
            if (fe.IsLoaded) Attach(fe);
            else fe.Loaded += OnLoaded;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            fe.Loaded -= OnLoaded;
            Attach(fe);
        }

        private static void Attach(FrameworkElement fe)
        {
            var sv = fe as ScrollViewer ?? FindScrollViewer(fe);
            if (sv == null) return;
            sv.ScrollChanged += (_, _) => Update(sv);
            Update(sv);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is ScrollViewer sv) return sv;
                if (FindScrollViewer(child) is { } found) return found;
            }
            return null;
        }

        private static void Update(ScrollViewer sv)
        {
            if (sv.Template?.FindName("PART_ScrollContentPresenter", sv) is not FrameworkElement content) return;
            // У ListBox смещения считаются в строках, а не в пикселях, — поэтому здесь
            // только сравнения, а высота затухания берётся из настоящей высоты содержимого.
            bool fadeTop = sv.VerticalOffset > 0.01;
            bool fadeBottom = sv.VerticalOffset < sv.ScrollableHeight - 0.01;
            double height = content.ActualHeight;
            if ((!fadeTop && !fadeBottom) || height <= 0)
            {
                content.OpacityMask = null;
                return;
            }
            double edge = Math.Min(1.0 / 3, FadeSize / height);
            // Координаты в пикселях, а не в долях: доли считаются от всего содержимого
            // вместе с невидимой прокрученной частью, и затухание уезжало за нижний край.
            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, height)
            };
            mask.GradientStops.Add(new GradientStop(fadeTop ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
            mask.GradientStops.Add(new GradientStop(fadeBottom ? Colors.Transparent : Colors.Black, 1));
            mask.Freeze();
            content.OpacityMask = mask;
        }
    }
}
