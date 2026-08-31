using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Views;

namespace Ven4Tools
{
    public partial class AlternativeSourceDialog : Window
    {
        public WingetPackage? SelectedPackage { get; private set; }
        public string? CustomUrl { get; private set; }
        public bool UseWingetFirst { get; private set; }
        public bool UseUrlFirst { get; private set; }

        private readonly string _appName;
        private bool _hasWingetResults = false;
        // Счётчик поколений фоновых поисков: поздний ответ применяется только если
        // остаётся последним запущенным, иначе он затирал бы более свежий результат.
        private int _searchGeneration;

        public AlternativeSourceDialog(string appName)
        {
            InitializeComponent();
            _appName = appName;
            // Через общий перехватчик, а не голой async void-лямбдой: сейчас
            // LoadWingetResultsAsync гасит всё сама, но любая будущая правка выше её
            // try сделает исключение неперехватываемым и уронит приложение.
            this.Loaded += (_, _) => TabInitGuard.Run(
                LoadWingetResultsAsync,
                "AlternativeSourceDialog.LoadWingetResultsAsync");
            chkPriorityWinget.IsEnabled = false;
        }

        private async Task LoadWingetResultsAsync()
        {
            int gen = ++_searchGeneration;
            pbSearch.Visibility = Visibility.Visible;
            cmbResults.IsEnabled = false;
            chkPriorityWinget.IsEnabled = false;

            try
            {
                var results = await WingetService.SearchAsync(_appName);

                // Пока шёл фоновый поиск, мог стартовать более новый — применяем только
                // результат последнего запроса.
                if (gen != _searchGeneration) return;

                // Пользователь мог начать вводить ID вручную, пока шёл поиск. Не затираем
                // его ввод и, что важнее, не сбрасываем _hasWingetResults=false при нуле
                // результатов — иначе валидный ручной ID становится несохраняемым, а
                // выбранный вручную пакет молча подменяется первым результатом поиска.
                bool manualEntered = !string.IsNullOrWhiteSpace(txtManualId.Text);

                if (!manualEntered)
                {
                    if (results.Count > 0)
                    {
                        _hasWingetResults = true;
                        // ObservableCollection, а не сырой List: при ручном вводе ID пакет
                        // вставляется в начало списка, и ComboBox должен это увидеть.
                        cmbResults.ItemsSource = new ObservableCollection<WingetPackage>(results);
                        cmbResults.SelectedIndex = 0;
                        cmbResults.IsEnabled = true;
                        chkPriorityWinget.IsEnabled = true;
                    }
                    else
                    {
                        _hasWingetResults = false;
                        cmbResults.IsEnabled = false;
                        chkPriorityWinget.IsEnabled = false;
                    }
                }

                pbSearch.Visibility = Visibility.Collapsed;
                CheckCanSave();
            }
            catch (TimeoutException)
            {
                if (gen != _searchGeneration) return;
                // winget завис и был завершён по таймауту — сообщаем немодально и
                // оставляем доступными ручной ввод ID и поле ссылки.
                pbSearch.Visibility = Visibility.Collapsed;
                if (string.IsNullOrWhiteSpace(txtManualId.Text))
                {
                    cmbResults.IsEnabled = false;
                    chkPriorityWinget.IsEnabled = false;
                }
                txtWingetLabel.Text = "⚠ Winget не ответил вовремя — введите ID вручную или укажите ссылку";
            }
            catch (Exception ex)
            {
                if (gen != _searchGeneration) return;
                pbSearch.Visibility = Visibility.Collapsed;
                if (string.IsNullOrWhiteSpace(txtManualId.Text))
                {
                    cmbResults.IsEnabled = false;
                    chkPriorityWinget.IsEnabled = false;
                }
                System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
            }
        }

        private void TxtManualId_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtManualId.Text))
            {
                CheckCanSave();
                return;
            }

            var manualPackage = new WingetPackage
            {
                Name = "Ручной ввод",
                Id = txtManualId.Text.Trim(),
                Version = "manual",
                Source = "manual"
            };

            // ItemsSource может быть null (поиск ничего не вернул) — тогда создаём
            // коллекцию и добавляем в неё ручной пакет, чтобы он реально попал в
            // выпадающий список и его можно было выбрать/сохранить.
            if (cmbResults.ItemsSource is not ObservableCollection<WingetPackage> list)
            {
                list = new ObservableCollection<WingetPackage>();
                cmbResults.ItemsSource = list;
            }

            var existing = list.FirstOrDefault(p => p.Id == manualPackage.Id);
            if (existing == null)
            {
                list.Insert(0, manualPackage);
                existing = manualPackage;
            }

            cmbResults.SelectedItem = existing;
            cmbResults.IsEnabled = true;
            // Флажок «Приоритет» обязан включаться вместе со списком. Он выключен в
            // конструкторе и включался только при непустом результате поиска — а при
            // пустом (winget ничего не нашёл, ради чего ручной ввод и существует)
            // оставался выключенным навсегда. При этом BtnOk_Click читает его значение
            // и для ручного ввода тоже: выключенный флажок нельзя отметить, поэтому
            // UseWingetFirst для ручного ID был всегда false, а обещание подписи
            // «Источник с галочкой „Приоритет“ будет использоваться первым» —
            // недостижимым именно в том сценарии, где оно нужнее всего.
            chkPriorityWinget.IsEnabled = true;
            _hasWingetResults = true;
            CheckCanSave();
        }

        private void CmbResults_SelectionChanged(object sender, SelectionChangedEventArgs e) => CheckCanSave();

        private void TxtUrl_TextChanged(object sender, TextChangedEventArgs e) => CheckCanSave();

        private void CheckCanSave()
        {
            btnOk.IsEnabled = (_hasWingetResults && cmbResults.SelectedItem != null) ||
                              !string.IsNullOrWhiteSpace(txtUrl.Text);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (_hasWingetResults && cmbResults.SelectedItem is WingetPackage selected)
            {
                string message = $"Вы выбрали:\n\n" +
                                 $"Название: {selected.Name}\n" +
                                 $"ID: {selected.Id}\n" +
                                 $"Версия: {selected.Version}\n\n" +
                                 $"Будет сохранён ID: {selected.Id}";

                var result = MessageBox.Show(message, "Подтверждение выбора",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No) return;

                SelectedPackage = selected;
                UseWingetFirst = chkPriorityWinget.IsChecked == true;
            }

            // Ручной ввод ID: если из выпадающего списка ничего валидного не выбрано
            // (например, поиск не дал результатов), но пользователь ввёл ID вручную —
            // используем его как альтернативный источник. Проверка идёт ДО отказа
            // «Выберите источник», иначе ручной ввод никогда не сохранялся бы.
            if (SelectedPackage == null && !string.IsNullOrWhiteSpace(txtManualId?.Text))
            {
                SelectedPackage = new WingetPackage
                {
                    Name = "Ручной ввод",
                    Id = txtManualId.Text.Trim(),
                    Version = "manual",
                    Source = "manual"
                };
                UseWingetFirst = chkPriorityWinget.IsChecked == true;
            }

            if (!string.IsNullOrWhiteSpace(txtUrl.Text))
            {
                string url = txtUrl.Text.Trim();
                if (!DownloadValidator.ValidateUrl(url))
                {
                    MessageBox.Show("Ссылка должна начинаться с https:// (незашифрованный http:// не поддерживается — установщик по нему нечем защитить от подмены)",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                CustomUrl = url;
                UseUrlFirst = chkPriorityUrl.IsChecked == true;
            }

            if (SelectedPackage == null && CustomUrl == null)
            {
                MessageBox.Show("Выберите источник или укажите ссылку",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ID будет подставлен в командную строку winget — проверяем допустимость
            // символов, чтобы исключить внедрение посторонних аргументов.
            if (SelectedPackage != null && !CommandLineGuard.ValidateId(SelectedPackage.Id))
            {
                MessageBox.Show(
                    "Недопустимый Winget ID: разрешены только буквы, цифры, точка, дефис, плюс и подчёркивание. " +
                    "Пробелов в идентификаторах winget не бывает — проверьте, не скопирован ли вместе с ID лишний текст.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                SelectedPackage = null;
                return;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
