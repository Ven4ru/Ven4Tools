using System.Windows;
using System.Windows.Media;

namespace Ven4Tools.Services
{
    public static class ThemeService
    {
        public static void Apply(string theme)
        {
            if (theme == "web") ApplyWeb();
            else if (theme == "teal") ApplyTeal();
            else ApplyDark(theme != "light");
        }

        public static void ApplyTeal()
        {
            var r = Application.Current.Resources;
            r["WindowBackground"]  = Brush(10,  10,  20);
            r["SidebarBackground"] = Brush(13,  16,  24);
            r["ContentBackground"] = Brush(10,  10,  20);
            r["CardBackground"]    = Brush(17,  22,  31);
            r["TextPrimary"]       = Brush(226, 232, 240);
            r["TextSecondary"]     = Brush(170, 184, 206);
            r["BorderBrush"]       = Brush(30,  42,  56);
            r["HeaderForeground"]  = Brush(226, 232, 240);
            r["AccentColor"]       = Brush(74,  222, 128);
        }

        public static void ApplyWeb()
        {
            var r = Application.Current.Resources;
            r["WindowBackground"]  = Brush(10,  22,  40);
            r["SidebarBackground"] = Brush(13,  31,  53);
            r["ContentBackground"] = Brush(8,   18,  32);
            r["CardBackground"]    = Brush(16,  30,  52);
            r["TextPrimary"]       = Brush(232, 240, 254);
            r["TextSecondary"]     = Brush(138, 155, 181);
            r["BorderBrush"]       = Brush(30,  50,  80);
            r["HeaderForeground"]  = Brush(232, 240, 254);
            r["AccentColor"]       = Brush(0,   230, 120);
        }

        public static void ApplyDark(bool isDark)
        {
            var r = Application.Current.Resources;
            if (isDark)
            {
                r["WindowBackground"]  = Brush(30,  30,  30);
                r["SidebarBackground"] = Brush(45,  45,  45);
                r["ContentBackground"] = Brush(37,  37,  38);
                r["CardBackground"]    = Brush(45,  45,  45);
                r["TextPrimary"]       = Brush(255, 255, 255);
                r["TextSecondary"]     = Brush(204, 204, 204);
                r["BorderBrush"]       = Brush(61,  61,  61);
                r["HeaderForeground"]  = Brush(255, 255, 255);
            }
            else
            {
                r["WindowBackground"]  = Brush(240, 240, 240);
                r["SidebarBackground"] = Brush(248, 248, 248);
                r["ContentBackground"] = Brush(245, 245, 245);
                r["CardBackground"]    = Brush(255, 255, 255);
                r["TextPrimary"]       = Brush(30,  30,  30);
                r["TextSecondary"]     = Brush(100, 100, 100);
                r["BorderBrush"]       = Brush(220, 220, 220);
                r["HeaderForeground"]  = Brush(30,  30,  30);
            }
            r["AccentColor"] = Brush(0, 120, 212);
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromRgb(r, g, b));
    }
}
