using System;
using System.Windows.Media;
using Ven4Tools.Helpers;

namespace Ven4Tools.Views.Tabs
{
    public sealed class LogEntry
    {
        // Строка «Центра активности» красится ключами темы, а не зашитой палитрой
        // Material: прежние #4CAF50/#FF9800/#00C3AA на белой карточке «Светлой»
        // темы давали контраст около 2:1 — журнал не читался. Смысл цвета сохранён
        // (успех/ошибка/предупреждение/информация/приглушённое), меняется оттенок.
        //
        // Цвет фиксируется в момент разбора строки: элементы, уже лежащие в списке,
        // при переключении темы не перекрашиваются — журнал живёт минутами и
        // заполняется заново, INPC ради этого не вводится.
        private const string KeySuccess = "StatusSuccess";
        private const string KeyDanger  = "StatusDanger";
        private const string KeyWarning = "StatusWarning";
        private const string KeyInfo    = "StatusInfo";
        private const string KeyMuted   = "TextSecondary";
        // Безопасность и лицензии (🛡️/🔑) выделяются акцентом темы: отдельного
        // фиолетового в палитре нет, а прежний #9C27B0 на тёмной подложке давал 2.4:1.
        private const string KeyAccent  = "AccentColor";

        // Резервные кисти на случай, если словаря ресурсов нет (юнит-тесты, дизайнер).
        private static readonly SolidColorBrush FallbackSuccess = Frozen(0x4C, 0xAF, 0x50);
        private static readonly SolidColorBrush FallbackDanger  = Frozen(0xF4, 0x43, 0x36);
        private static readonly SolidColorBrush FallbackWarning = Frozen(0xFF, 0x98, 0x00);
        private static readonly SolidColorBrush FallbackInfo    = Frozen(0x00, 0xC3, 0xAA);
        private static readonly SolidColorBrush FallbackMuted   = Frozen(0x6B, 0x8C, 0xAE);
        private static readonly SolidColorBrush FallbackAccent  = Frozen(0x9C, 0x27, 0xB0);

        private static Brush BrushGreen  => BrushResolver.Resolve(KeySuccess, FallbackSuccess);
        private static Brush BrushRed    => BrushResolver.Resolve(KeyDanger,  FallbackDanger);
        private static Brush BrushOrange => BrushResolver.Resolve(KeyWarning, FallbackWarning);
        private static Brush BrushTeal   => BrushResolver.Resolve(KeyInfo,    FallbackInfo);
        private static Brush BrushMuted  => BrushResolver.Resolve(KeyMuted,   FallbackMuted);
        private static Brush BrushPurple => BrushResolver.Resolve(KeyAccent,  FallbackAccent);

        public string Time      { get; }
        public string Icon      { get; }
        public string Message   { get; }
        // Красится только значок: текст сообщения наследует TextPrimary напрямую
        // через DynamicResource (MainWindow.xaml, шаблон строки журнала). Парная
        // кисть AccentBrush была ровно копией IconBrush и не биндилась нигде —
        // убрана как мёртвая.
        public Brush IconBrush  { get; }

        private LogEntry(string time, string icon, string message, Brush iconBrush)
        {
            Time = time; Icon = icon; Message = message;
            IconBrush = iconBrush;
        }

        public static LogEntry Parse(string raw)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string text = raw.TrimStart();

            (string icon, string msg, Brush iconBrush) =
                text switch
                {
                    _ when text.StartsWith("✅") => ("✅", text[2..].TrimStart(), BrushGreen),
                    _ when text.StartsWith("❌") => ("❌", text[2..].TrimStart(), BrushRed),
                    _ when text.StartsWith("⚠️") => ("⚠️", text[3..].TrimStart(), BrushOrange),
                    _ when text.StartsWith("➕") => ("➕", text[2..].TrimStart(), BrushOrange),
                    _ when text.StartsWith("🗑️") => ("🗑️", text[3..].TrimStart(), BrushOrange),
                    _ when text.StartsWith("📦") => ("📦", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("📡") => ("📡", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("💾") => ("💾", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("📅") => ("📅", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("📋") => ("📋", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("📥") => ("📥", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("📤") => ("📤", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("🔍") => ("🔍", text[2..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("ℹ️") => ("ℹ️", text[3..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("☁️") => ("☁️", text[3..].TrimStart(), BrushTeal),
                    _ when text.StartsWith("🔄") => ("🔄", text[2..].TrimStart(), BrushMuted),
                    _ when text.StartsWith("⏳") => ("⏳", text[2..].TrimStart(), BrushMuted),
                    _ when text.StartsWith("🔔") => ("🔔", text[2..].TrimStart(), BrushMuted),
                    _ when text.StartsWith("🆙") => ("🆙", text[2..].TrimStart(), BrushGreen),
                    _ when text.StartsWith("🛡️") => ("🛡️", text[3..].TrimStart(), BrushPurple),
                    _ when text.StartsWith("🔑") => ("🔑", text[2..].TrimStart(), BrushPurple),
                    _                            => ("·",  text,                  BrushMuted),
                };

            return new LogEntry(time, icon, msg, iconBrush);
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
