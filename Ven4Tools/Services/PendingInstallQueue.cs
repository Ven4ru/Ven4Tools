using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ven4Tools.Helpers;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Очередь установки, переживающая перезагрузку.
    ///
    /// Перед началом пачки её состав записывается в файл; каждое установленное
    /// приложение из файла вычёркивается, а после окончания пачки файл удаляется.
    /// Остаться он может только в одном случае — если клиент не дошёл до конца:
    /// компьютер перезагрузили (сам установщик или пользователь), клиент закрыли или он
    /// упал. При следующем запуске клиент спрашивает, продолжать ли, и по согласию
    /// ставит оставшееся.
    ///
    /// Сам после перезагрузки клиент не запускается: он работает с правами
    /// администратора, и автозапуск означал бы запрос UAC сразу при входе в систему.
    /// </summary>
    public sealed class PendingInstallQueue
    {
        /// <summary>Очередь старше этого срока не предлагается: человек о ней уже не помнит.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

        public static PendingInstallQueue Default { get; } = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "pending_install.json"));

        public sealed record Pending(IReadOnlyList<string> AppIds, string? InstallDrive, DateTime StartedUtc);

        private readonly string _path;
        private readonly object _gate = new();

        public PendingInstallQueue(string path) => _path = path;

        /// <summary>Начало пачки: что предстоит установить.</summary>
        public void Begin(IEnumerable<string> appIds, string? installDrive)
        {
            lock (_gate)
            {
                Write(new FileModel
                {
                    AppIds = appIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    InstallDrive = installDrive,
                    StartedUtc = DateTime.UtcNow
                });
            }
        }

        /// <summary>Приложение установлено — из очереди оно уходит.</summary>
        public void MarkDone(string appId)
        {
            lock (_gate)
            {
                var model = Read();
                if (model == null) return;
                model.AppIds.RemoveAll(id => string.Equals(id, appId, StringComparison.OrdinalIgnoreCase));
                if (model.AppIds.Count == 0) Delete();
                else Write(model);
            }
        }

        /// <summary>Пачка дошла до конца (или её отменили) — продолжать нечего.</summary>
        public void Clear()
        {
            lock (_gate) Delete();
        }

        /// <summary>
        /// Незавершённая очередь прошлого запуска; null, если её нет, она пуста,
        /// устарела или файл не читается (испорченный файл заодно убирается).
        /// </summary>
        public Pending? Load(DateTime nowUtc)
        {
            lock (_gate)
            {
                var model = Read();
                if (model == null) return null;

                var ids = model.AppIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                bool stale = nowUtc - model.StartedUtc > MaxAge || model.StartedUtc > nowUtc.AddDays(1);
                if (ids.Count == 0 || stale)
                {
                    Delete();
                    return null;
                }
                return new Pending(ids, model.InstallDrive, model.StartedUtc);
            }
        }

        private FileModel? Read()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path));
                if (model?.AppIds == null)
                {
                    Delete();
                    return null;
                }
                return model;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[PendingInstallQueue] Очередь не прочитана и сброшена: {ex.Message}");
                Delete();
                return null;
            }
        }

        private void Write(FileModel model)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                FileHelper.WriteAllTextAtomic(_path, JsonSerializer.Serialize(model));
            }
            catch (Exception ex)
            {
                // Очередь — удобство, а не условие установки: не записалась, значит
                // после перезагрузки просто не будет предложения продолжить.
                AppLogger.Write($"[PendingInstallQueue] Очередь не записана: {ex.Message}");
            }
        }

        private void Delete()
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { AppLogger.Write($"[PendingInstallQueue] Очередь не удалена: {ex.Message}"); }
        }

        private sealed class FileModel
        {
            public List<string> AppIds { get; set; } = new();
            public string? InstallDrive { get; set; }
            public DateTime StartedUtc { get; set; }
        }
    }
}
