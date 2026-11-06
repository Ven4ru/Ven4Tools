using System.Globalization;
using System.Text;
using Avalonia.Media;

namespace Ven4Tools.Linux.Views;

// Значки бокового меню и шапки. Значки Segoe MDL2 из клиента Windows в Linux недоступны,
// поэтому здесь простые фигуры в сетке 24×24 с той же идеей: сетка — каталог, стопка — наборы и т. д.
public static class Icons
{
    public static readonly Geometry Catalog = Geometry.Parse("M3,3H10V10H3Z M14,3H21V10H14Z M3,14H10V21H3Z M14,14H21V21H14Z");
    public static readonly Geometry Sets = Geometry.Parse(
        "M12,2L22,7L12,12L2,7Z M4,10.9L12,14.9L20,10.9L22,12L12,17L2,12Z M4,15.9L12,19.9L20,15.9L22,17L12,22L2,17Z");
    public static readonly Geometry Updates = Geometry.Parse(
        "M11,3H13V12.2L16.6,8.6L18,10L12,16L6,10L7.4,8.6L11,12.2Z M4,18H20V21H4Z");
    public static readonly Geometry Look = Geometry.Parse(
        "M3,17.25V21H6.75L17.81,9.94L14.06,6.19Z M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.13,5.12L18.88,8.87Z");
    public static readonly Geometry Computer = Geometry.Parse(
        "F0 M2,3H22V17H14V19H17V21H7V19H10V17H2Z M4,5V15H20V5Z");
    public static readonly Geometry Autostart = Geometry.Parse(
        "M11,2H13V12H11Z M17.8,5.2L16.4,6.6C17.98,7.9 19,9.83 19,12A7,7 0 0,1 12,19A7,7 0 0,1 5,12C5,9.83 6.02,7.9 7.6,6.6L6.2,5.2C4.25,6.84 3,9.27 3,12A9,9 0 0,0 12,21A9,9 0 0,0 21,12C21,9.27 19.75,6.84 17.8,5.2Z");
    public static readonly Geometry ChevronRight = Geometry.Parse("M8,4L17,12L8,20Z");
    public static readonly Geometry ChevronDown = Geometry.Parse("M4,8L20,8L12,17Z");
    public static readonly Geometry Gear = MakeGear(teeth: 8, outer: 10.5, inner: 8, hole: 3.4);

    // Звезда избранного. Символы ★/☆ в Linux берутся из запасного шрифта, и там ☆ бывает закрашенной —
    // избранное нельзя было отличить, поэтому звёзды нарисованы фигурами
    public static readonly Geometry StarFilled = Geometry.Parse(StarPath(10, 4.2));
    public static readonly Geometry StarOutline = Geometry.Parse("F0 " + StarPath(10, 4.2) + " " + StarPath(7.2, 3));

    private static string StarPath(double outer, double inner)
    {
        var sb = new StringBuilder("M");
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = -Math.PI / 2 + i * Math.PI / 5;
            if (i > 0) sb.Append(" L");
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{12 + r * Math.Cos(a):0.###},{12.6 + r * Math.Sin(a):0.###}"));
        }
        return sb.Append('Z').ToString();
    }

    // Шестерёнка: зубцы по окружности и отверстие в центре (заливка «чёт-нечет»)
    private static Geometry MakeGear(int teeth, double outer, double inner, double hole)
    {
        static string P(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.###},{y:0.###}");
        const double c = 12;
        var sb = new StringBuilder("F0 M");
        var step = Math.PI * 2 / teeth;
        for (var i = 0; i < teeth; i++)
        {
            var a = i * step;
            // Вершина зубца занимает треть шага, по бокам — скат к впадине
            foreach (var (r, da) in new[] { (inner, -step * 0.30), (outer, -step * 0.17), (outer, step * 0.17), (inner, step * 0.30) })
            {
                sb.Append(i == 0 && da < -step * 0.2 ? "" : " L");
                sb.Append(P(c + r * Math.Cos(a + da), c + r * Math.Sin(a + da)));
            }
        }
        sb.Append('Z');
        sb.Append(" M").Append(P(c + hole, c))
          .Append(" A").Append(P(hole, hole)).Append(" 0 1 0 ").Append(P(c - hole, c))
          .Append(" A").Append(P(hole, hole)).Append(" 0 1 0 ").Append(P(c + hole, c)).Append('Z');
        return Geometry.Parse(sb.ToString());
    }
}
