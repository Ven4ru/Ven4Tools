using System;
using System.Windows;
using System.Windows.Controls;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Раскладывает карточки ровными колонками на всю ширину: колонок столько, сколько
    /// помещается при ширине не меньше <see cref="MinItemWidth"/>, оставшееся место
    /// делится между ними поровну. WrapPanel с фиксированной шириной карточки оставлял
    /// справа пустую полосу почти в целую карточку при любом размере окна.
    /// </summary>
    public sealed class AdaptiveCardPanel : Panel
    {
        public double MinItemWidth { get; set; } = 220;
        public double Gap { get; set; } = 12;

        protected override Size MeasureOverride(Size availableSize)
        {
            bool unbounded = double.IsInfinity(availableSize.Width);
            int count = InternalChildren.Count;
            double width = unbounded ? MinItemWidth * Math.Max(1, count) + Gap * Math.Max(0, count - 1) : availableSize.Width;
            var (columns, itemWidth) = Columns(width);

            double total = 0, rowHeight = 0;
            int column = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (++column == columns)
                {
                    total += rowHeight + Gap;
                    rowHeight = 0;
                    column = 0;
                }
            }
            if (column > 0) total += rowHeight + Gap;

            return new Size(width, Math.Max(0, total - Gap));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (columns, itemWidth) = Columns(finalSize.Width);
            int count = InternalChildren.Count;
            double y = 0;

            for (int start = 0; start < count; start += columns)
            {
                int end = Math.Min(start + columns, count);
                double rowHeight = 0;
                for (int i = start; i < end; i++)
                    rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);

                // Высота у всех карточек ряда одна — по самой высокой: кнопки внизу
                // карточек встают на одну линию.
                for (int i = start; i < end; i++)
                    InternalChildren[i].Arrange(new Rect((i - start) * (itemWidth + Gap), y, itemWidth, rowHeight));

                y += rowHeight + Gap;
            }

            return finalSize;
        }

        private (int Columns, double ItemWidth) Columns(double width)
        {
            int columns = Math.Max(1, (int)Math.Floor((width + Gap) / (MinItemWidth + Gap)));
            double itemWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
            return (columns, itemWidth);
        }
    }
}
