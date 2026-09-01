using System.Windows.Media;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private void LoadOfflineSettings()
        {
            SetField(ref _offlineMode, ProfileService.Current.OfflineMode, nameof(OfflineMode));
            SetField(ref _forceOnlineMode, ProfileService.Current.ForceOnlineMode, nameof(ForceOnlineMode));
            SetField(ref _paranoidMode, ProfileService.Current.ParanoidMode, nameof(ParanoidMode));

            string cachePath = ProfileService.Current.OfflineCachePath;
            if (string.IsNullOrEmpty(cachePath)) cachePath = OfflineService.CacheBasePath;
            SetField(ref _offlineCachePathText, cachePath, nameof(OfflineCachePathText));
        }

        private void SaveOfflineSettings()
        {
            ProfileService.Current.OfflineCachePath = OfflineCachePathText.Trim();
            ProfileService.Save();
        }

        private bool _offlineMode;
        public bool OfflineMode
        {
            get => _offlineMode;
            set
            {
                if (_offlineMode == value) return;
                SetField(ref _offlineMode, value);
                ProfileService.Current.OfflineMode = value;
                ProfileService.Save();
                RefreshTabVisibility?.Invoke();
                UpdateConnectivityStatus();
            }
        }

        private bool _forceOnlineMode;
        public bool ForceOnlineMode
        {
            get => _forceOnlineMode;
            set
            {
                if (_forceOnlineMode == value) return;
                SetField(ref _forceOnlineMode, value);
                ProfileService.Current.ForceOnlineMode = value;
                ProfileService.Save();
                RefreshTabVisibility?.Invoke();
                UpdateConnectivityStatus();
            }
        }

        private bool _paranoidMode;
        public bool ParanoidMode
        {
            get => _paranoidMode;
            set
            {
                if (_paranoidMode == value) return;
                SetField(ref _paranoidMode, value);
                ProfileService.Current.ParanoidMode = value;
                ProfileService.Save();
            }
        }

        // Сохранение — только по LostFocus (см. UpdateSourceTrigger=LostFocus в XAML),
        // не на каждое нажатие клавиши, ровно как в оригинале (txtOfflineCachePath.LostFocus).
        private string _offlineCachePathText = "";
        public string OfflineCachePathText
        {
            get => _offlineCachePathText;
            set
            {
                if (_offlineCachePathText == value) return;
                SetField(ref _offlineCachePathText, value);
                SaveOfflineSettings();
            }
        }

        private string _connIconText = "🟢";
        public string ConnIconText { get => _connIconText; private set => SetField(ref _connIconText, value); }

        private string _connStatusText = "Интернет доступен";
        public string ConnStatusText { get => _connStatusText; private set => SetField(ref _connStatusText, value); }

        // Дефолт — прозрачная подложка: в оригинальном XAML у pnlConnStatus нет
        // статичного Background, цвет всегда выставлялся программно из
        // UpdateConnectivityStatus().
        //
        // Хранится КЛЮЧ статуса, а не готовая кисть: кисть замешивается из цвета
        // текущей темы разовым TryFindResource (см. StatusTint), и запомнить её
        // значило бы оставить плашку в цвете темы, активной на момент последней
        // смены состояния сети. Ключ переживает переключение темы.
        private string? _connStatusTintKey;
        public Brush ConnStatusBackground =>
            _connStatusTintKey is null ? Brushes.Transparent : StatusTint(_connStatusTintKey);

        public void UpdateConnectivityStatus()
        {
            bool online        = ConnectivityMonitor.IsOnline;
            bool offlineForced = ProfileService.Current.OfflineMode;
            bool onlineForced  = ProfileService.Current.ForceOnlineMode;

            if (offlineForced)
            {
                ConnIconText = "🟡";
                ConnStatusText = "Принудительный офлайн — вкладки скрыты вручную";
                SetConnStatusTint("StatusWarning");
            }
            else if (!online && onlineForced)
            {
                ConnIconText = "🟠";
                ConnStatusText = "Соединение не обнаружено, но онлайн-режим принудительно включён";
                SetConnStatusTint("StatusWarning");
            }
            else if (!online)
            {
                ConnIconText = "🔴";
                ConnStatusText = "Интернет недоступен — онлайн-вкладки скрыты";
                SetConnStatusTint("StatusDanger");
            }
            else
            {
                ConnIconText = "🟢";
                ConnStatusText = "Интернет доступен — все вкладки активны";
                SetConnStatusTint("StatusSuccess");
            }
            ConnectivityStatusUpdated?.Invoke();
        }

        private void SetConnStatusTint(string statusResourceKey)
        {
            _connStatusTintKey = statusResourceKey;
            OnPropertyChanged(nameof(ConnStatusBackground));
        }

        /// <summary>Перечитать подложку плашки состояния сети после смены темы.</summary>
        private void RefreshThemeBrushes() => OnPropertyChanged(nameof(ConnStatusBackground));

        /// <summary>
        /// Полупрозрачная подложка плашки состояния сети. Раньше здесь были четыре
        /// зашитых тёмных цвета (тёмно-жёлтый/оранжевый/красный/зелёный) — на белой
        /// карточке «Светлой» темы тёмная плашка с тёмным текстом не читалась.
        /// Теперь берётся цвет статуса текущей темы и разбавляется до подложки:
        /// поверх тёмного фона получается приглушённый оттенок, поверх светлого —
        /// пастельный, а текст сверху в обоих случаях остаётся контрастным.
        /// </summary>
        private static Brush StatusTint(string statusResourceKey)
        {
            if (BrushResolver.Resolve(statusResourceKey, Brushes.Transparent) is not SolidColorBrush status)
                return Brushes.Transparent;

            Color c = status.Color;
            var brush = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }
}
