namespace Ven4Tools.Launcher.Models
{
    public class Notification
    {
        public string Id      { get; set; } = "";
        public string Title   { get; set; } = "";
        public string Message { get; set; } = "";
        // Английский вариант заголовка и текста; в старых файлах уведомлений его нет.
        public string TitleEn { get; set; } = "";
        public string MessageEn { get; set; } = "";
        public string Type    { get; set; } = "info";
    }
}
