using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace Ven4Tools.Localization
{
    /// <summary>
    /// Подстановка перевода в окна программы.
    ///
    /// Исходники остаются русскими, а перевод применяется в одном месте — там, где текст
    /// попадает на экран. Почти весь текст WPF в итоге рисует <see cref="TextBlock"/>
    /// (подпись кнопки, заголовок вкладки, подсказка, строка списка), поэтому достаточно
    /// следить за ним: при первой раскладке элемента и при каждой смене текста строка ищется в
    /// языковом пакете. Заголовки окон, поля только для чтения (журналы) и подписи для
    /// программ чтения с экрана обрабатываются так же.
    ///
    /// Пока пакет не включён (русский язык), ничего не подключается и не выполняется —
    /// русская версия работает ровно как раньше.
    ///
    /// Значение подменяется через <c>SetCurrentValue</c>: привязка данных остаётся живой,
    /// и следующее значение из модели снова придёт сюда и будет переведено.
    /// </summary>
    public static class UiTranslator
    {
        private static LanguagePack? _pack;
        private static bool _hooked;
        private static ConcurrentDictionary<string, byte>? _missed;
        private static string? _collectPath;
        private static bool _collectOnly;
        private const string CollectVariable = "VEN4TOOLS_L10N_COLLECT";

        /// <summary>Запрошен ли режим сбора строк без перевода (переменная окружения с именем файла).</summary>
        public static bool CollectRequested =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CollectVariable));

        public static bool IsActive => _pack != null && !_collectOnly;

        /// <summary>Язык включённого пакета; «ru», если перевод не включён.</summary>
        public static string Language => IsActive ? _pack!.Language : "ru";

        /// <summary>Перевод строки для мест, где текст уходит мимо окон WPF (системные диалоги, трей).</summary>
        public static string Tr(string? text)
        {
            if (_pack == null) return text ?? "";
            string translated;
            try { translated = _pack.Translate(text); }
            catch (Exception ex)
            {
                // Сбой перевода не должен мешать показать сообщение: оно выйдет как есть.
                if (System.Threading.Interlocked.Increment(ref _failures) <= 5)
                {
                    try { Log?.Invoke($"[Язык] Сбой при переводе надписи: {ex.GetType().Name}: {ex.Message}"); }
                    catch (Exception) { }
                }
                return text ?? "";
            }
            // Только сбор: строка сверена с пакетом, но на экран идёт русский текст.
            return _collectOnly ? text ?? "" : translated;
        }

        /// <summary>
        /// Включает перевод. Вызывать в потоке интерфейса до создания первого окна.
        /// Повторный вызов заменяет пакет: уже показанные окна останутся как есть.
        /// </summary>
        /// <param name="collectOnly">
        /// Интерфейс остаётся русским, а каждая показанная строка сверяется с пакетом
        /// (см. режим сбора ниже). Так обычные проверки интерфейса заодно находят
        /// строки, которым не хватает перевода.
        /// </param>
        public static void Activate(LanguagePack pack, bool collectOnly = false)
        {
            _collectOnly = collectOnly;
            // Режим сбора: переменная окружения называет файл, куда складываются русские
            // строки, оставшиеся без перевода. Нужен при подготовке пакета, не пользователю.
            string? collect = Environment.GetEnvironmentVariable(CollectVariable);
            if (!string.IsNullOrWhiteSpace(collect) && _missed == null)
            {
                _collectPath = collect;
                _missed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            }
            // Каждая новая строка пишется в файл сразу: проверки интерфейса завершают
            // программу принудительно, и до события выхода из процесса дело не доходит.
            if (_missed != null) pack.Missed = RecordMissed;

            _pack = pack;
            if (_hooked) return;
            _hooked = true;
            // Точка входа — первое изменение размера элемента, то есть его первая раскладка.
            // Событие Loaded для этого не годится: WPF рассылает его только тем элементам,
            // у которых (или у чьих потомков) есть собственный обработчик Loaded, а общий
            // обработчик класса в расчёт не берёт. Размер же получает всё, что видно на
            // экране, и происходит это до первой отрисовки — русский текст не мелькает.
            EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnTextBlockSized));
            EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnControlSized));
        }

        /// <summary>Куда сообщать о сбое перевода (журнал программы). Программа от него не падает.</summary>
        public static Action<string>? Log { get; set; }

        private static int _failures;

        // Перевод работает внутри раскладки и отрисовки окна: исключение отсюда — это
        // аварийное завершение всей программы из-за надписи. Любой сбой гасится, надпись
        // остаётся как есть, в журнал уходят первые несколько случаев.
        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Increment(ref _failures) <= 5)
                {
                    try { Log?.Invoke($"[Язык] Сбой при переводе надписи: {ex.GetType().Name}: {ex.Message}"); }
                    catch (Exception) { }
                }
            }
        }

        // Элемент уже подключён к переводу: событие размера приходит много раз, работа нужна один.
        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(UiTranslator), new PropertyMetadata(false));

        private static bool TakeHook(DependencyObject element)
        {
            if ((bool)element.GetValue(HookedProperty)) return false;
            element.SetValue(HookedProperty, true);
            return true;
        }

        private static readonly object CollectLock = new();

        private static void RecordMissed(string text)
        {
            if (_missed == null || _collectPath == null || !_missed.TryAdd(text, 0)) return;
            string line = text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + Environment.NewLine;
            try
            {
                lock (CollectLock) File.AppendAllText(_collectPath, line, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ── TextBlock ────────────────────────────────────────────────────────────

        // Наблюдатель за текстом: привязка «сам к себе» живёт вместе с элементом и не
        // держит его в памяти, в отличие от подписки через DependencyPropertyDescriptor.
        private static readonly DependencyProperty WatchedTextProperty = DependencyProperty.RegisterAttached(
            "WatchedText", typeof(string), typeof(UiTranslator), new PropertyMetadata(null, OnWatchedTextChanged));

        // Последний подставленный перевод: по нему отличаем собственную подмену от нового значения.
        private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
            "Applied", typeof(string), typeof(UiTranslator), new PropertyMetadata(null));

        private static void OnTextBlockSized(object sender, SizeChangedEventArgs e) => Guard(() => OnTextBlockSizedCore(sender, e));

        private static void OnTextBlockSizedCore(object sender, SizeChangedEventArgs e)
        {
            if (_pack == null || sender is not TextBlock block || !TakeHook(block)) return;
            if (HasInlineContent(block))
            {
                // Текст с оформлением собран из кусков — переводится по кускам, свойство
                // Text трогать нельзя: его запись стёрла бы оформление.
                TranslateInlines(block.Inlines);
                return;
            }
            Watch(block, TextBlock.TextProperty);
        }

        // Простой текст TextBlock хранит строкой; куски (Run, Span, Hyperlink) — логическими
        // потомками. Обращение к Inlines само переводит элемент в «сложный» режим, поэтому
        // сначала смотрим на логических потомков.
        private static bool HasInlineContent(TextBlock block)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(block))
            {
                if (child is not string) return true;
            }
            return false;
        }

        private static void TranslateInlines(InlineCollection inlines)
        {
            // По копии списка: подстановка перевода в кусок меняет содержимое элемента, и
            // перечисление самой коллекции на этом обрывается исключением.
            foreach (var inline in inlines.ToList())
            {
                if (inline is Run run)
                {
                    // Кусок может быть привязан к данным и меняться после показа — за ним
                    // следим так же, как за текстом целого элемента.
                    if (BindingOperations.GetBindingExpressionBase(run, WatchedTextProperty) == null)
                    {
                        BindingOperations.SetBinding(run, WatchedTextProperty, new Binding
                        {
                            Path = new PropertyPath(Run.TextProperty),
                            RelativeSource = RelativeSource.Self,
                            Mode = BindingMode.OneWay
                        });
                    }
                    else ApplyTo(run, Run.TextProperty, run.Text);
                }
                else if (inline is Span span) TranslateInlines(span.Inlines);
            }
        }

        private static void OnWatchedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Guard(() => OnWatchedTextChangedCore(d, e));

        private static void OnWatchedTextChangedCore(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (_pack == null || e.NewValue is not string text) return;
            if (d is TextBlock block) ApplyTo(block, TextBlock.TextProperty, text);
            else if (d is Window window) ApplyTo(window, Window.TitleProperty, text);
            else if (d is TextBox box) ApplyTo(box, TextBox.TextProperty, text);
            else if (d is Run run) ApplyTo(run, Run.TextProperty, text);
        }

        private static void ApplyTo(DependencyObject target, DependencyProperty property, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if ((string?)target.GetValue(AppliedProperty) == text) return;   // это наша собственная подмена
            string translated = Tr(text);
            if (ReferenceEquals(translated, text) || translated == text) return;
            target.SetValue(AppliedProperty, translated);
            target.SetCurrentValue(property, translated);
        }

        // ── Окна, поля только для чтения, подписи для программ чтения с экрана ───

        private static void OnControlSized(object sender, SizeChangedEventArgs e) => Guard(() => OnControlSizedCore(sender, e));

        private static void OnControlSizedCore(object sender, SizeChangedEventArgs e)
        {
            if (_pack == null || sender is not Control control || !TakeHook(control)) return;

            if (control is Window window) Watch(window, Window.TitleProperty);
            // Только поля для чтения: журналы и отчёты. То, что вводит человек, не трогаем.
            else if (control is TextBox { IsReadOnly: true } box) Watch(box, TextBox.TextProperty);

            TranslateProperty(control, AutomationProperties.NameProperty);
            TranslateProperty(control, AutomationProperties.HelpTextProperty);
            WatchSpokenName(control);
        }

        private static void Watch(FrameworkElement element, DependencyProperty property)
        {
            if (BindingOperations.GetBindingExpressionBase(element, WatchedTextProperty) != null) return;
            element.SetBinding(WatchedTextProperty, new Binding
            {
                Path = new PropertyPath(property),
                RelativeSource = RelativeSource.Self,
                Mode = BindingMode.OneWay
            });
        }

        // ── Имя элемента для программ чтения с экрана ────────────────────────────
        //
        // Кнопка с подписью-строкой называет себя этой строкой, а не тем, что нарисовал
        // TextBlock внутри неё: на экране уже перевод, а диктор читал бы исходный текст.
        // Саму подпись (Content, Header) не трогаем — код вправе сверяться с ней, — а
        // переведённое имя задаём явно и обновляем вместе с подписью.

        private static readonly DependencyProperty WatchedCaptionProperty = DependencyProperty.RegisterAttached(
            "WatchedCaption", typeof(object), typeof(UiTranslator), new PropertyMetadata(null, OnWatchedCaptionChanged));

        private static readonly DependencyProperty SpokenNameOwnedProperty = DependencyProperty.RegisterAttached(
            "SpokenNameOwned", typeof(bool), typeof(UiTranslator), new PropertyMetadata(false));

        private static void WatchSpokenName(Control control)
        {
            DependencyProperty? caption = control switch
            {
                HeaderedContentControl => HeaderedContentControl.HeaderProperty,
                HeaderedItemsControl => HeaderedItemsControl.HeaderProperty,
                Window => null,                       // окно называет себя заголовком
                ContentControl => ContentControl.ContentProperty,
                _ => null
            };
            if (caption == null) return;
            if (BindingOperations.GetBindingExpressionBase(control, WatchedCaptionProperty) != null) return;
            // Имя, заданное автором разметки, главнее подписи: оно уже переведено выше.
            if (!string.IsNullOrEmpty(AutomationProperties.GetName(control))) return;
            // Подпись-строка либо привязка, которая может её дать; готовое дерево элементов
            // (значок с текстом) называет себя само.
            if (control.GetValue(caption) is not string && !BindingOperations.IsDataBound(control, caption)) return;

            control.SetBinding(WatchedCaptionProperty, new Binding
            {
                Path = new PropertyPath(caption),
                RelativeSource = RelativeSource.Self,
                Mode = BindingMode.OneWay
            });
        }

        private static void OnWatchedCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Guard(() => OnWatchedCaptionChangedCore(d, e));

        private static void OnWatchedCaptionChangedCore(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (_pack == null) return;
            string? translated = null;
            if (e.NewValue is string text && text.Length > 0)
            {
                string result = Tr(text);
                if (!ReferenceEquals(result, text) && result != text) translated = result;
            }

            if (translated != null)
            {
                d.SetValue(SpokenNameOwnedProperty, true);
                d.SetCurrentValue(AutomationProperties.NameProperty, translated);
            }
            else if ((bool)d.GetValue(SpokenNameOwnedProperty))
            {
                // Подпись сменилась на ту, что перевода не требует: имя снова берётся из неё.
                d.SetValue(SpokenNameOwnedProperty, false);
                d.ClearValue(AutomationProperties.NameProperty);
            }
        }

        private static void TranslateProperty(DependencyObject target, DependencyProperty property)
        {
            if (target.ReadLocalValue(property) is not string text || text.Length == 0) return;
            string translated = Tr(text);
            if (!ReferenceEquals(translated, text) && translated != text)
                target.SetCurrentValue(property, translated);
        }
    }

    /// <summary>
    /// Короткое имя перевода для кода: <c>Tr("…")</c> там, где текст уходит мимо окон WPF —
    /// в системные диалоги выбора файла, меню и подсказки значка в трее, имена файлов.
    /// Подключается на весь проект через <c>global using static</c>.
    /// </summary>
    public static class TranslationShortcut
    {
        public static string Tr(string? text) => UiTranslator.Tr(text);
    }
}
