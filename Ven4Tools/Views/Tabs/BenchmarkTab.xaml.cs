using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Вкладка «Бенчмарк»: измерение скорости накопителя, определение подключения и отчёт.
    /// Полностью офлайн — сетевых вызовов здесь нет ни одного.
    /// </summary>
    public partial class BenchmarkTab : UserControl
    {
        private bool _initialized;

        private List<PhysicalDiskInfo> _disks = new List<PhysicalDiskInfo>();
        private PhysicalDiskInfo? _selectedDisk;
        private BenchmarkVolumeInfo? _selectedVolume;
        private List<string> _warnings = new List<string>();

        /// <summary>Номер последнего запроса предупреждений — защита от наложения вызовов.</summary>
        private int _warningsToken;

        private BenchmarkRunResult? _lastResult;
        private CancellationTokenSource? _cancellation;
        private bool _running;

        public BenchmarkTab()
        {
            InitializeComponent();

            cmbDisks.SelectionChanged += CmbDisks_SelectionChanged;
            cmbVolumes.SelectionChanged += CmbVolumes_SelectionChanged;
            cmbFileSize.SelectionChanged += CmbFileSize_SelectionChanged;
            btnRunBenchmark.Click += BtnRunBenchmark_Click;
            btnCopyReport.Click += BtnCopyReport_Click;
            btnSaveReport.Click += BtnSaveReport_Click;

            Loaded += BenchmarkTab_Loaded;
        }

        private async void BenchmarkTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            FillFileSizes();

            // Подчистка тестовых файлов, оставшихся от аварийно прерванных прогонов.
            DiskBenchmarkEngine.CleanupOrphanedFiles();

            await LoadDisksAsync();
        }

        private void FillFileSizes()
        {
            cmbFileSize.Items.Clear();
            foreach (long size in DiskBenchmarkEngine.FileSizes)
            {
                cmbFileSize.Items.Add(new ComboBoxItem
                {
                    Content = BenchmarkReportBuilder.FormatBinarySize(size),
                    Tag = size
                });
            }
            cmbFileSize.SelectedIndex = 0;
        }

        /// <summary>Размер тестового файла, выбранный пользователем.</summary>
        private long SelectedFileSize =>
            cmbFileSize.SelectedItem is ComboBoxItem item && item.Tag is long size
                ? size
                : DiskBenchmarkEngine.FileSizes[0];

        /// <summary>Профиль прогона, выбранный пользователем.</summary>
        private BenchmarkProfile SelectedProfile
        {
            get
            {
                string tag = (cmbProfile.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Normal";
                return tag switch
                {
                    "Fast" => BenchmarkProfile.Fast,
                    "Precise" => BenchmarkProfile.Precise,
                    _ => BenchmarkProfile.Normal
                };
            }
        }
    }
}
