using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class OfficeTab : UserControl
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();
        private CancellationTokenSource? _cancellationTokenSource;
        private string? _downloadedFilePath;

        // Сохранённое состояние региона (Office CC и Windows GeoID)
        private string? _originalOfficeCC;   // исходное значение из ExperimentConfigs\Ecs\CountryCode
        private string? _originalGeoName;    // например "RU" из Control Panel\International\Geo\Name
        private string? _originalGeoNation;  // например "203" из Control Panel\International\Geo\Nation

        // Persistent-маркер региона на диске — страховка от hard-kill / отключения питания
        // между SetRegionUS() и RestoreRegion(). Если процесс убит, файл переживёт и регион
        // будет восстановлен при следующем запуске (см. конструктор).
        private static readonly string _regionBackupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ven4Tools", "region_backup.json");

        public event Action? GoToActivation;

        private readonly string[] officeLanguages = { "ru-ru", "en-us", "de-de", "fr-fr", "es-es", "it-it", "zh-cn", "ja-jp" };

        private readonly Dictionary<string, string> officeDirectLinks = new()
        {
            { "O365ProPlusRetail",       "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=O365ProPlusRetail&platform=x64&language={0}&version=O16GA" },
            { "ProPlus2024Retail",       "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlus2024Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2021Retail",  "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2021Retail&platform=x64&language={0}&version=O16GA" },
            { "Professional2019Retail",  "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=Professional2019Retail&platform=x64&language={0}&version=O16GA" },
            { "ProPlusRetail",           "https://c2rsetup.officeapps.live.com/c2r/download.aspx?ProductreleaseID=ProPlusRetail&platform=x64&language={0}&version=O16GA" }
        };

        public OfficeTab()
        {
            InitializeComponent();

            // Восстановление региона после аварийного завершения (hard-kill / отключение питания
            // во время установки Office, когда finally в BtnInstallOffice_Click не успел отработать).
            RecoverRegionFromBackup();

            FillComboBoxes();

            btnDownloadOffice.Click += BtnDownloadOffice_Click;
            btnInstallOffice.Click  += BtnInstallOffice_Click;
            btnCancelOffice.Click   += (_, _) =>
            {
                _cancellationTokenSource?.Cancel();
                btnCancelOffice.IsEnabled = false;
                AppLogger.Write("⏹️ Запрос отмены...");
            };
            btnGoActivation.Click += (_, _) => GoToActivation?.Invoke();

            // M2: смена версии/языка после скачивания должна сбрасывать уже скачанный
            // установщик — иначе «Установить» тихо поставит старую версию/язык, тогда как
            // лог/UI показывают новое выбранное значение. Подписки — после FillComboBoxes,
            // чтобы начальный SelectedIndex=0 не срабатывал как «смена».
            rdbO365.Checked  += OnOfficeSelectionChanged;
            rdbO2024.Checked += OnOfficeSelectionChanged;
            rdbO2021.Checked += OnOfficeSelectionChanged;
            rdbO2019.Checked += OnOfficeSelectionChanged;
            rdbO2016.Checked += OnOfficeSelectionChanged;
            cmbOfficeLanguage.SelectionChanged += OnOfficeSelectionChanged;

            pnlActivationHint.Visibility = Visibility.Visible;
            UpdateRegionDisplay();
        }

        // M2: при смене версии/языка удаляем ранее скачанный установщик и блокируем
        // «Установить», чтобы нельзя было поставить не то, что показано в UI.
        private void OnOfficeSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_downloadedFilePath == null) return;

            try { if (File.Exists(_downloadedFilePath)) File.Delete(_downloadedFilePath); } catch { }
            _downloadedFilePath = null;
            btnInstallOffice.IsEnabled = false;
            AppLogger.Write("ℹ️ Версия/язык изменены — скачайте установщик заново");
            SetProgress(true, "ℹ️ Версия/язык изменены — скачайте установщик заново", 0, "");
        }

        // ── Вспомогательные методы ────────────────────────────────────────────

        private (string DisplayName, string ProductId) GetSelectedVersion()
        {
            if (rdbO2024.IsChecked == true) return ("Office 2024 ProPlus",     "ProPlus2024Retail");
            if (rdbO2021.IsChecked == true) return ("Office 2021 Professional", "Professional2021Retail");
            if (rdbO2019.IsChecked == true) return ("Office 2019 Professional", "Professional2019Retail");
            if (rdbO2016.IsChecked == true) return ("Office 2016 Professional", "ProPlusRetail");
            return ("Office 365 ProPlus", "O365ProPlusRetail");
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0");
            client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            return client;
        }

        private void FillComboBoxes()
        {
            cmbOfficeLanguage.ItemsSource   = officeLanguages;
            cmbOfficeLanguage.SelectedIndex = 0;
        }

        private void SetProgress(bool visible, string phase = "", double value = 0, string detail = "")
        {
            Dispatcher.Invoke(() =>
            {
                pnlProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                txtInstallPhase.Text   = phase;
                progressOffice.Value   = value;
                txtInstallDetail.Text  = detail;
            });
        }

        private void SetPhase(string text) =>
            Dispatcher.Invoke(() => txtInstallPhase.Text = text);

        private void SetDetail(string text) =>
            Dispatcher.Invoke(() => txtInstallDetail.Text = text);

    }
}
