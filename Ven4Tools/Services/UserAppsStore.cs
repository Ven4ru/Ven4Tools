using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Хранилище приложений, добавленных пользователем вручную (apps.json), вместе со
    /// всей DPAPI-обвязкой: защита файла, разовая миграция со старого незащищённого
    /// формата и маркер этой миграции.
    ///
    /// Выделено из AppManager: это самая нагруженная смыслом часть прежнего класса
    /// (модель угроз, fail-closed при подмене файла), и держать её вперемешку с
    /// альтернативными источниками и скрытыми приложениями означало каждый раз
    /// перечитывать всё сразу. Список приложений хранилище не держит — только читает
    /// и пишет его по запросу владельца.
    /// </summary>
    public sealed class UserAppsStore
    {
        private readonly string _configPath;
        private readonly bool _protect;
        // Файл есть, но не прочитан (занят, нет доступа) — см. Load и Save.
        private bool _loadFailed;

        // Дополнительная энтропия DPAPI: привязывает защищённый blob именно к этому
        // назначению, а не к любому DPAPI-контейнеру той же учётной записи.
        private static readonly byte[] AppsEntropy = Encoding.UTF8.GetBytes("Ven4Tools.apps.v1");

        /// <param name="configPath">Путь к apps.json.</param>
        /// <param name="protect">
        /// apps.json защищается через DPAPI (привязка к учётной записи Windows) только
        /// в обычном режиме — файл в user-writable %LocalAppData% иначе можно подменить
        /// ДРУГИМ ПОЛЬЗОВАТЕЛЕМ Windows, а клиент прочитал бы подделанные URL/аргументы
        /// установки без проверки целостности. Модель угроз ограничена именно этим:
        /// DataProtectionScope.CurrentUser доступен любому процессу ТОГО ЖЕ пользователя
        /// (Entropy ниже — открытая константа), так что от вредоносного кода, уже
        /// работающего от имени того же аккаунта, DPAPI здесь не защищает — это
        /// осознанно недорогая защита от подмены файла, а не крипто-барьер против
        /// компрометации той же учётной записи. В переносимом режиме файл лежит рядом
        /// с exe и обязан переноситься между машинами/учётками — DPAPI это сломало бы,
        /// поэтому там сохраняется обычный JSON (модель угроз переносимого носителя иная).
        /// </param>
        public UserAppsStore(string configPath, bool protect)
        {
            _configPath = configPath;
            _protect = protect;
        }

        // Маркер факта миграции apps.json на DPAPI. Существование = защищённый файл
        // сохранялся на этой машине хотя бы раз. После этого приём legacy-plaintext при
        // загрузке закрывается (см. Load): иначе локальный процесс мог бы вечно
        // обходить DPAPI, подсовывая plaintext вместо защищённого файла.
        private string MigrationMarkerPath => _configPath + ".dpapi";

        /// <summary>
        /// Читает сохранённые пользовательские приложения. При отсутствии файла,
        /// нечитаемом содержимом или отклонении незащищённого ввода возвращает
        /// пустой список — приложение при этом не падает.
        /// </summary>
        public List<AppInfo> Load()
        {
            try
            {
                var read = SettingsFileReader.TryRead(_configPath, "[UserAppsStore]", out string raw);
                // Файл есть, но занят: владелец получит пустой список, и первое же
                // сохранение (добавили приложение, сменили альтернативный источник)
                // стёрло бы все добавленные вручную приложения — см. Save.
                _loadFailed = read == SettingsReadResult.Unreadable;
                if (read != SettingsReadResult.Read) return new List<AppInfo>();
                if (string.IsNullOrWhiteSpace(raw)) return new List<AppInfo>();

                List<AppInfo>? userApps;
                bool needsUpgrade = false;

                if (_protect)
                {
                    // Сначала пробуем снять DPAPI-защиту.
                    userApps = TryUnprotect(raw);
                    if (userApps != null)
                    {
                        // Файл действительно защищён — фиксируем факт миграции (восстанавливаем
                        // маркер, если его удалили), чтобы дальше действовал fail-closed.
                        EnsureMigrationMarker();
                    }
                    else
                    {
                        // Снятие DPAPI не удалось. Голый JSON (legacy-формат прежних версий)
                        // принимаем ТОЛЬКО пока миграция ещё не состоялась — разовое окно
                        // апгрейда. Если маркер уже есть, защищённый файл сохранялся хотя бы
                        // раз, и plaintext на его месте — подмена: отклоняем fail-closed
                        // (пустой список user apps, приложение не падает).
                        if (File.Exists(MigrationMarkerPath))
                        {
                            AppLogger.Write("[UserAppsStore] apps.json не расшифровывается DPAPI, но миграция уже выполнена — незащищённый ввод отклонён (fail-closed)");
                            return new List<AppInfo>();
                        }
                        userApps = TryParsePlain(raw);
                        if (userApps != null) needsUpgrade = true;
                    }
                }
                else
                {
                    userApps = TryParsePlain(raw);
                }

                if (userApps == null) return new List<AppInfo>();

                if (needsUpgrade)
                {
                    AppLogger.Write("[UserAppsStore] apps.json в старом незащищённом формате — миграция в DPAPI");
                    Save(userApps.Where(a => a.IsUserAdded).ToList());
                }

                return userApps;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[UserAppsStore] Load: {ex.Message}");
                return new List<AppInfo>();
            }
        }

        public void Save(List<AppInfo> userApps)
        {
            try
            {
                if (_loadFailed)
                {
                    // При старте файл не прочитался, и у владельца в памяти нет того,
                    // что лежит на диске. Дочитываем сейчас и дописываем к сохраняемому
                    // списку всё, чего в нём нет: запись без этого стёрла бы прежние
                    // приложения. Не читается и сейчас — не пишем вовсе.
                    var onDisk = Load();
                    if (_loadFailed)
                    {
                        AppLogger.Write("[UserAppsStore] Сохранение пропущено: apps.json не удалось прочитать, запись стёрла бы добавленные приложения");
                        return;
                    }
                    var known = new HashSet<string>(userApps.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
                    userApps = userApps.Concat(onDisk.Where(a => a.IsUserAdded && known.Add(a.Id))).ToList();
                }

                FileHelper.WriteAllTextAtomic(_configPath, Serialize(userApps));
                // Защищённый файл записан — фиксируем миграцию, чтобы при следующей
                // загрузке plaintext на его месте больше не принимался автоматически.
                if (_protect) EnsureMigrationMarker();
            }
            catch (Exception ex) { AppLogger.Write($"[UserAppsStore] Save: {ex.Message}"); }
        }

        private void EnsureMigrationMarker()
        {
            try
            {
                if (!File.Exists(MigrationMarkerPath))
                    FileHelper.WriteAllTextAtomic(MigrationMarkerPath, "1");
            }
            catch (Exception ex) { AppLogger.Write($"[UserAppsStore] EnsureMigrationMarker: {ex.Message}"); }
        }

        private static List<AppInfo>? TryUnprotect(string raw)
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(raw.Trim());
                var plainBytes = ProtectedData.Unprotect(
                    protectedBytes, AppsEntropy, DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<List<AppInfo>>(Encoding.UTF8.GetString(plainBytes));
            }
            catch { return null; }
        }

        private static List<AppInfo>? TryParsePlain(string raw)
        {
            try { return JsonConvert.DeserializeObject<List<AppInfo>>(raw); }
            catch { return null; }
        }

        private string Serialize(List<AppInfo> userApps)
        {
            var json = JsonConvert.SerializeObject(userApps, Formatting.Indented);
            if (!_protect) return json;
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), AppsEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
    }
}
