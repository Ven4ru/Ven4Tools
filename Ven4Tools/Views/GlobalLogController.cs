using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools.Views
{
    /// <summary>
    /// «Центр активности» главного окна: буфер сообщений журнала, его ограничение по
    /// размеру, очистка и копирование в буфер обмена. Вынесено из главного окна целиком
    /// вместе со списком — так политика хранения (лимит записей) и формат копирования
    /// перестают быть частью кода окна.
    /// <para>
    /// Класс живёт в слое представления, а не в <c>Services</c>, намеренно:
    /// <see cref="LogEntry"/> несёт готовые WPF-кисти, и сервис, зависящий от типов
    /// представления, перевернул бы направление зависимостей.
    /// </para>
    /// </summary>
    public sealed class GlobalLogController
    {
        /// <summary>Сколько последних сообщений держим на экране.</summary>
        private const int MaxEntries = 500;

        private readonly ListBox _list;
        private readonly ObservableCollection<LogEntry> _entries = new();

        public GlobalLogController(ListBox list)
        {
            _list = list;
            _list.ItemsSource = _entries;
        }

        /// <summary>
        /// Добавляет сообщение в журнал. Вызывается из фоновых потоков (подписка на
        /// <c>AppLogger.MessageReceived</c>), поэтому переключение в поток UI — здесь.
        /// </summary>
        public void Append(string message)
        {
            _list.Dispatcher.Invoke(() =>
            {
                var entry = LogEntry.Parse(message);
                _entries.Add(entry);
                while (_entries.Count > MaxEntries) _entries.RemoveAt(0);
                _list.ScrollIntoView(entry);
            });
        }

        public void Clear() => _entries.Clear();

        /// <summary>
        /// Копирует выделенные строки, а если выделения нет — весь журнал.
        /// </summary>
        public void CopySelectedOrAll()
        {
            var items = _list.SelectedItems.Count > 0
                ? _list.SelectedItems.Cast<LogEntry>()
                : _entries.AsEnumerable();
            var text = string.Join(Environment.NewLine,
                items.Select(entry => $"[{entry.Time}] {entry.Icon} {entry.Message}"));
            if (string.IsNullOrEmpty(text)) return;

            // Буфер обмена в Windows — единый на всю систему ресурс: пока им владеет
            // другой процесс (менеджер буфера, удалённый рабочий стол), SetText бросает
            // COMException. Без перехвата это исключение уходило из обработчика нажатия
            // и роняло приложение — та же защита уже стоит у копирования отчётов
            // «Бенчмарка» и «Диагностики».
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "GlobalLogController.CopySelectedOrAll");
                MessageBox.Show("Не удалось скопировать журнал: " + ex.Message, "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
