using System;
using System.Collections.Generic;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class NetworkViewModel
    {
        // ── Адаптеры ─────────────────────────────────────────────────────────

        private IReadOnlyList<AdapterInfo> _adapters = Array.Empty<AdapterInfo>();
        public IReadOnlyList<AdapterInfo> Adapters
        {
            get => _adapters;
            private set => SetField(ref _adapters, value);
        }

        private bool _adaptersEmpty;
        public bool AdaptersEmpty
        {
            get => _adaptersEmpty;
            private set => SetField(ref _adaptersEmpty, value);
        }

        private void RefreshAdapters()
        {
            var adapters = DiagnosticsService.GetAdapters();
            Adapters = adapters;
            AdaptersEmpty = adapters.Count == 0;
            AppLogger.Write($"[Сеть] Адаптеров: {adapters.Count}");
        }
    }
}
