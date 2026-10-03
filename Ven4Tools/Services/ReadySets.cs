using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Готовые наборы программ для экрана «Обзор»: типичный состав «для чего этот
    /// компьютер». Набор только отмечает программы в каталоге — состав можно поправить
    /// перед установкой, сама установка начинается кнопкой «Установить».
    ///
    /// Идентификаторы — из каталога (Catalog/master.json). Если программа из набора в
    /// каталоге не найдена или недоступна, она пропускается, а пользователю об этом
    /// сообщается; соответствие каталогу проверяет юнит-тест.
    /// </summary>
    public sealed record ReadySet(string Key, string Title, string Description, IReadOnlyList<string> AppIds);

    public static class ReadySets
    {
        public static IReadOnlyList<ReadySet> All { get; } = new[]
        {
            new ReadySet("home", "Для дома",
                "Браузер, мессенджер, видео, архиватор, документы и PDF.",
                new[] { "firefox", "telegram", "vlc", "7zip", "onlyoffice", "sumatra-pdf" }),
            new ReadySet("work", "Для работы",
                "Браузер, офис, PDF, видеосвязь, мессенджер, снимки экрана и удалённый доступ.",
                new[] { "google-chrome", "onlyoffice", "adobe-acrobat-reader", "zoom", "telegram", "7zip", "greenshot", "anydesk" }),
            new ReadySet("games", "Для игр",
                "Магазины игр, голосовой чат, запись и мониторинг видеокарты.",
                new[] { "steam", "epic-games", "discord", "7zip", "obs-studio", "msi-afterburner" }),
            new ReadySet("dev", "Для разработки",
                "Редактор кода, Git, Python, Node.js, Postman и Notepad++.",
                new[] { "vscode", "git", "python", "nodejs", "postman", "notepad-plus-plus", "7zip", "google-chrome" })
        };

        public static ReadySet? Find(string key) => All.FirstOrDefault(set => set.Key == key);
    }
}
