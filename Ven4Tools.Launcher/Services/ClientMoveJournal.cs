using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Что сделать при старте с остатками прерванного переноса клиента.</summary>
    internal enum ClientMoveRecoveryAction
    {
        /// <summary>Указатель не относится ни к старой, ни к новой папке — ничего не трогаем.</summary>
        None,

        /// <summary>Указатель остался на старой папке: перенос не состоялся, новая копия
        /// (и временная папка) удаляются, клиент работает из старой.</summary>
        RemoveNewCopy,

        /// <summary>Указатель уже на новой (проверенной) папке: не успели удалить старую.</summary>
        RemoveOldCopy,
    }

    /// <summary>
    /// Журнал переноса клиента: что откуда и куда переносится. Пишется ДО первого
    /// изменения на диске и удаляется после полного завершения. Если лаунчер убит
    /// или ПК выключился посередине, журнал при следующем старте говорит, что
    /// доделать или убрать, а решение принимается по указателю из настроек
    /// (ClientPath): именно он определяет, откуда запускается клиент.
    /// </summary>
    internal sealed class ClientMoveJournal
    {
        public string OldClientPath { get; set; } = "";
        public string NewClientPath { get; set; } = "";
        public string StagingPath { get; set; } = "";

        /// <summary>
        /// Решение по журналу и текущему указателю. Чистая функция — покрыта тестами.
        /// </summary>
        internal static ClientMoveRecoveryAction Decide(ClientMoveJournal journal, string currentClientPath)
        {
            if (ClientFolderMover.PathsEqual(currentClientPath, journal.NewClientPath))
                return ClientMoveRecoveryAction.RemoveOldCopy;
            if (ClientFolderMover.PathsEqual(currentClientPath, journal.OldClientPath))
                return ClientMoveRecoveryAction.RemoveNewCopy;
            return ClientMoveRecoveryAction.None;
        }

        public static ClientMoveJournal? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var journal = JsonSerializer.Deserialize<ClientMoveJournal>(File.ReadAllText(path, Encoding.UTF8));
                if (journal == null ||
                    string.IsNullOrWhiteSpace(journal.OldClientPath) ||
                    string.IsNullOrWhiteSpace(journal.NewClientPath))
                    return null;
                return journal;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Атомарная запись (как настройки). Исключение — наружу: без
        /// журнала перенос не начинается.</summary>
        public void Save(string path) =>
            Helpers.FileHelper.WriteAllTextAtomic(path, JsonSerializer.Serialize(this));

        public static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Оставшийся журнал безвреден: при следующем старте решение по нему
                // будет «ничего не делать» или то же самое, что уже сделано.
            }
        }
    }
}
