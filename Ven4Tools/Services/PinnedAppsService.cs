using System.Collections.Generic;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Хранилище закреплённых приложений (панель «пинов» в шапке рабочей области).
    /// Раньше эти операции были статическими методами главного окна, хотя к окну
    /// отношения не имеют: это состояние профиля, а не UI. Отрисовку панели делает
    /// <see cref="Views.PinsStripController"/>.
    /// </summary>
    public static class PinnedAppsService
    {
        /// <summary>Максимум закреплённых приложений — больше не помещается в полосу.</summary>
        public const int MaxPins = 6;

        /// <summary>Идентификаторы закреплённых приложений в порядке добавления.</summary>
        public static IReadOnlyList<string> Pinned => ProfileService.Current.PinnedAppIds;

        public static bool IsPinned(string id) =>
            ProfileService.Current.PinnedAppIds.Contains(id);

        /// <summary>Закрепляет приложение. Повторное закрепление и превышение лимита игнорируются.</summary>
        public static void Pin(string id)
        {
            var pins = ProfileService.Current.PinnedAppIds;
            if (pins.Contains(id) || pins.Count >= MaxPins) return;
            pins.Add(id);
            ProfileService.Save();
        }

        public static void Unpin(string id)
        {
            ProfileService.Current.PinnedAppIds.Remove(id);
            ProfileService.Save();
        }
    }
}
