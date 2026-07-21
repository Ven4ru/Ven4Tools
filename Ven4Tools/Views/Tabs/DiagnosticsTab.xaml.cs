using System;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class DiagnosticsTab : UserControl
    {
        private bool _initialized = false;

        public DiagnosticsTab()
        {
            InitializeComponent();

            // Обработчики всех кнопок вкладки: информация о системе, логи, Turbo Boost,
            // очистка кэша Windows Update, запуск диагностики и экспорт полного отчёта.
            btnCopySystemInfo.Click += BtnCopySystemInfo_Click;
            btnOpenLogs.Click += BtnOpenLogs_Click;
            btnOpenLatestLog.Click += BtnOpenLatestLog_Click;
            btnClearLogs.Click += BtnClearLogs_Click;
            btnDisableTurboBoost.Click += BtnDisableTurboBoost_Click;
            btnEnableTurboBoost.Click += BtnEnableTurboBoost_Click;
            btnClearWuCache.Click += BtnClearWuCache_Click;
            btnRunDiagnostics.Click += BtnRunDiagnostics_Click;
            btnCopyFullReport.Click += BtnCopyFullReport_Click;

            Loaded += DiagnosticsTab_Loaded;
        }

        private async void DiagnosticsTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            await LoadSystemInfoAsync();
            await RefreshTurboBoostStatusAsync();
        }
    }
}
