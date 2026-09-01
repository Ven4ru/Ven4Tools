using System;
using System.ComponentModel;
using System.Windows.Media;
using Ven4Tools.Helpers;

namespace Ven4Tools.Views.Tabs
{
    public sealed class LogEntry : INotifyPropertyChanged
    {
        // Строка «Центра активности» красится ключами темы, а не зашитой палитрой
        // Material: прежние #4CAF50/#FF9800/#00C3AA на белой карточке «Светлой»
        // темы давали контраст около 2:1 — журнал не читался. Смысл цвета сохранён
        // (успех/ошибка/предупреждение/информация/приглушённое), меняется оттенок.
        //
        // Запись держит КЛЮЧ темы, а не готовую кисть: BrushResolver делает разовый
        // TryFindResource, и запомненная кисть навсегда оставила бы значок в цвете
        // темы, активной в момент разбора строки. «Центр активности» доступен из
        // главного окна всегда и накапливает до 500 сообщений за сеанс — прежнее
        // допущение «журнал живёт минутами и заполняется заново» на деле означало,
        // что после переключения темы весь уже написанный журнал оставался в цветах
        // прежней (значки успеха «Тёмной» темы на белой карточке «Светлой»).
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

        // Пара «ключ темы + резервная кисть» вместо готовой кисти — набор тот же,
        // что был у прежних Brush-свойств, разбор строки ниже не изменился.
        private static readonly (string Key, Brush Fallback) Green  = (KeySuccess, FallbackSuccess);
        private static readonly (string Key, Brush Fallback) Red    = (KeyDanger,  FallbackDanger);
        private static readonly (string Key, Brush Fallback) Orange = (KeyWarning, FallbackWarning);
        private static readonly (string Key, Brush Fallback) Teal   = (KeyInfo,    FallbackInfo);
        private static readonly (string Key, Brush Fallback) Muted  = (KeyMuted,   FallbackMuted);
        private static readonly (string Key, Brush Fallback) Purple = (KeyAccent,  FallbackAccent);

        private readonly string _iconBrushKey;
        private readonly Brush _iconBrushFallback;

        public string Time      { get; }
        public string Icon      { get; }
        public string Message   { get; }
        // Красится только значок: текст сообщения наследует TextPrimary напрямую
        // через DynamicResource (MainWindow.xaml, шаблон строки журнала). Парная
        // кисть AccentBrush была ровно копией IconBrush и не биндилась нигде —
        // убрана как мёртвая.
        public Brush IconBrush => BrushResolver.Resolve(_iconBrushKey, _iconBrushFallback);

        /// <summary>
        /// Единственное изменяемое в записи — цвет значка, и меняется он не сам по
        /// себе, а по смене темы. INPC нужен именно поэтому: список журнала
        /// виртуализирован, но уже отрисованные строки WPF заново не опрашивает,
        /// и без уведомления вычисляемый геттер перечитывался бы только у тех
        /// строк, которые успели уйти за границу видимости и вернуться.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Перечитать цвет значка по прежнему ключу после смены темы.
        /// <para>
        /// Запись не подписывается на <c>ThemeService.ThemeChanged</c> сама: записей
        /// до 500 и они постоянно вытесняются новыми — подписка каждой на
        /// статическое событие удерживала бы весь журнал от сборки мусора. Обходит
        /// коллекцию единственный долгоживущий владелец,
        /// <see cref="Ven4Tools.Views.GlobalLogController"/> — тот же приём, что у
        /// строк каталога (<c>AppRowViewModel.RefreshThemeBrushes</c>).
        /// </para>
        /// </summary>
        internal void RefreshThemeBrushes() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBrush)));

        private LogEntry(string time, string icon, string message, (string Key, Brush Fallback) iconBrush)
        {
            Time = time; Icon = icon; Message = message;
            _iconBrushKey = iconBrush.Key;
            _iconBrushFallback = iconBrush.Fallback;
        }

        public static LogEntry Parse(string raw)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string text = raw.TrimStart();

            (string icon, string msg, (string Key, Brush Fallback) iconBrush) =
                text switch
                {
                    _ when text.StartsWith("✅") => ("✅", text[2..].TrimStart(), Green),
                    _ when text.StartsWith("❌") => ("❌", text[2..].TrimStart(), Red),
                    _ when text.StartsWith("⚠️") => ("⚠️", text[3..].TrimStart(), Orange),
                    _ when text.StartsWith("➕") => ("➕", text[2..].TrimStart(), Orange),
                    _ when text.StartsWith("🗑️") => ("🗑️", text[3..].TrimStart(), Orange),
                    _ when text.StartsWith("📦") => ("📦", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("📡") => ("📡", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("💾") => ("💾", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("📅") => ("📅", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("📋") => ("📋", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("📥") => ("📥", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("📤") => ("📤", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("🔍") => ("🔍", text[2..].TrimStart(), Teal),
                    _ when text.StartsWith("ℹ️") => ("ℹ️", text[3..].TrimStart(), Teal),
                    _ when text.StartsWith("☁️") => ("☁️", text[3..].TrimStart(), Teal),
                    _ when text.StartsWith("🔄") => ("🔄", text[2..].TrimStart(), Muted),
                    _ when text.StartsWith("⏳") => ("⏳", text[2..].TrimStart(), Muted),
                    _ when text.StartsWith("🔔") => ("🔔", text[2..].TrimStart(), Muted),
                    _ when text.StartsWith("🆙") => ("🆙", text[2..].TrimStart(), Green),
                    _ when text.StartsWith("🛡️") => ("🛡️", text[3..].TrimStart(), Purple),
                    _ when text.StartsWith("🔑") => ("🔑", text[2..].TrimStart(), Purple),
                    _                            => ("·",  text,                  Muted),
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
