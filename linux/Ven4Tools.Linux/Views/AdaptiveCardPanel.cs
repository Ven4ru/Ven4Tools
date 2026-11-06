using Avalonia;
using Avalonia.Controls;

namespace Ven4Tools.Linux.Views;

// Сетка карточек, как AdaptiveCardPanel клиента Windows: столько колонок, сколько влезает
// при ширине не меньше MinItemWidth, карточки растягиваются на всю строку без пустого края.
public sealed class AdaptiveCardPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveCardPanel, double>(nameof(MinItemWidth), 208);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<AdaptiveCardPanel, double>(nameof(Gap), 12);

    static AdaptiveCardPanel() => AffectsMeasure<AdaptiveCardPanel>(MinItemWidthProperty, GapProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0) width = MinItemWidth * 3 + Gap * 2;
        var columns = Math.Max(1, (int)((width + Gap) / (MinItemWidth + Gap)));
        return (columns, Math.Max(0, (width - Gap * (columns - 1)) / columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Layout(availableSize.Width);
        double height = 0, rowHeight = 0;
        var visible = Children.Where(c => c.IsVisible).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == visible.Count - 1)
            {
                height += rowHeight + (i == visible.Count - 1 ? 0 : Gap);
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(availableSize.Width) ? itemWidth * columns + Gap * (columns - 1) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Layout(finalSize.Width);
        var visible = Children.Where(c => c.IsVisible).ToList();
        double y = 0;
        for (var row = 0; row * columns < visible.Count; row++)
        {
            var cells = visible.Skip(row * columns).Take(columns).ToList();
            var rowHeight = cells.Max(c => c.DesiredSize.Height);
            for (var col = 0; col < cells.Count; col++)
                cells[col].Arrange(new Rect(col * (itemWidth + Gap), y, itemWidth, rowHeight));
            y += rowHeight + Gap;
        }
        return finalSize;
    }
}
