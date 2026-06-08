using System;
using System.Windows.Media;

namespace Ven4Tools.Views.Tabs
{
    public sealed class LogEntry
    {
        private static readonly SolidColorBrush BrushGreen  = Frozen(0x4C, 0xAF, 0x50);
        private static readonly SolidColorBrush BrushRed    = Frozen(0xF4, 0x43, 0x36);
        private static readonly SolidColorBrush BrushOrange = Frozen(0xFF, 0x98, 0x00);
        private static readonly SolidColorBrush BrushTeal   = Frozen(0x00, 0xC3, 0xAA);
        private static readonly SolidColorBrush BrushMuted  = Frozen(0x6B, 0x8C, 0xAE);
        private static readonly SolidColorBrush BrushPurple = Frozen(0x9C, 0x27, 0xB0);

        public string Time        { get; }
        public string Icon        { get; }
        public string Message     { get; }
        public SolidColorBrush IconBrush   { get; }
        public SolidColorBrush AccentBrush { get; }

        private LogEntry(string time, string icon, string message,
                         SolidColorBrush iconBrush, SolidColorBrush accentBrush)
        {
            Time = time; Icon = icon; Message = message;
            IconBrush = iconBrush; AccentBrush = accentBrush;
        }

        public static LogEntry Parse(string raw)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string text = raw.TrimStart();

            (string icon, string msg, SolidColorBrush iconBrush, SolidColorBrush accent) =
                text switch
                {
                    _ when text.StartsWith("✅") => ("✅", text[2..].TrimStart(), BrushGreen,  BrushGreen),
                    _ when text.StartsWith("❌") => ("❌", text[2..].TrimStart(), BrushRed,    BrushRed),
                    _ when text.StartsWith("⚠️") => ("⚠️", text[3..].TrimStart(), BrushOrange, BrushOrange),
                    _ when text.StartsWith("➕") => ("➕", text[2..].TrimStart(), BrushOrange, BrushOrange),
                    _ when text.StartsWith("🗑️") => ("🗑️", text[3..].TrimStart(), BrushOrange, BrushOrange),
                    _ when text.StartsWith("📦") => ("📦", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📡") => ("📡", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("💾") => ("💾", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📅") => ("📅", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📋") => ("📋", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📥") => ("📥", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("📤") => ("📤", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("🔍") => ("🔍", text[2..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("ℹ️") => ("ℹ️", text[3..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("☁️") => ("☁️", text[3..].TrimStart(), BrushTeal,   BrushTeal),
                    _ when text.StartsWith("🔄") => ("🔄", text[2..].TrimStart(), BrushMuted,  BrushMuted),
                    _ when text.StartsWith("⏳") => ("⏳", text[2..].TrimStart(), BrushMuted,  BrushMuted),
                    _ when text.StartsWith("🔔") => ("🔔", text[2..].TrimStart(), BrushMuted,  BrushMuted),
                    _ when text.StartsWith("🆙") => ("🆙", text[2..].TrimStart(), BrushGreen,  BrushGreen),
                    _ when text.StartsWith("🛡️") => ("🛡️", text[3..].TrimStart(), BrushPurple, BrushPurple),
                    _ when text.StartsWith("🔑") => ("🔑", text[2..].TrimStart(), BrushPurple, BrushPurple),
                    _                            => ("·",  text,                  BrushMuted,  BrushMuted),
                };

            return new LogEntry(time, icon, msg, iconBrush, accent);
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
