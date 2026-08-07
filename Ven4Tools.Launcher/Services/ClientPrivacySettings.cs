using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Чтение приватных настроек клиента из его profile.json.
    /// Лаунчер — отдельное приложение и своего «параноидального режима» не имеет,
    /// но публикует отчёты клиента (краш, неудачные установки) ПУБЛИЧНЫМ issue на
    /// GitHub. Флажок «Параноидальный режим» в клиенте обещает блокировать отправку
    /// краш-отчётов и отзывов — и это обещание нельзя выполнить только со стороны
    /// клиента: клиент удаляет отложенный отчёт при СВОЁМ старте, а лаунчер
    /// показывает предложение опубликовать его при СВОЁМ, то есть обычно раньше.
    /// Поэтому лаунчер сверяется с тем же флагом напрямую.
    /// </summary>
    internal static class ClientPrivacySettings
    {
        private static string ProfilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "profile.json");

        /// <summary>
        /// Включён ли в клиенте параноидальный режим. Файла нет / он повреждён —
        /// считаем, что выключен (клиент ни разу не сохранял настройки).
        /// </summary>
        public static bool IsParanoidMode()
        {
            try
            {
                string path = ProfilePath;
                if (!File.Exists(path)) return false;
                var profile = JObject.Parse(File.ReadAllText(path));
                return profile["ParanoidMode"]?.Value<bool>() == true;
            }
            catch
            {
                return false;
            }
        }
    }
}
