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
    /// следить за ним: при появлении элемента и при каждой смене текста строка ищется в
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
            string translated = _pack.Translate(text);
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
                pack.Missed = text => _missed.TryAdd(text, 0);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushMissed();
            }

            _pack = pack;
            if (_hooked) return;
            _hooked = true;
            EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTextBlockLoaded));
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTextBoxLoaded));
            EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnControlLoaded));
        }

        /// <summary>Сбрасывает собранные непереведённые строки в файл (режим сбора).</summary>
        public static void FlushMissed()
        {
            if (_missed == null || _collectPath == null) return;
            try
            {
                var lines = _missed.Keys.Select(k => k.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n"));
                File.AppendAllLines(_collectPath, lines, new UTF8Encoding(false));
                _missed.Clear();
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

        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
        {
            if (_pack == null || sender is not TextBlock block) return;
            if (HasInlineContent(block))
            {
                // Текст с оформлением собран из кусков — переводится по кускам, свойство
                // Text трогать нельзя: его запись стёрла бы оформление.
                TranslateInlines(block.Inlines);
                return;
            }
            if (BindingOperations.GetBindingExpressionBase(block, WatchedTextProperty) == null)
            {
                block.SetBinding(WatchedTextProperty, new Binding
                {
                    Path = new PropertyPath(TextBlock.TextProperty),
                    RelativeSource = RelativeSource.Self,
                    Mode = BindingMode.OneWay
                });
            }
            else ApplyTo(block, TextBlock.TextProperty, block.Text);
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
            foreach (var inline in inlines)
            {
                if (inline is Run run)
                {
                    string text = run.Text;
                    string translated = Tr(text);
                    if (!ReferenceEquals(translated, text) && translated != text)
                        run.SetCurrentValue(Run.TextProperty, translated);
                }
                else if (inline is Span span) TranslateInlines(span.Inlines);
            }
        }

        private static void OnWatchedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (_pack == null || e.NewValue is not string text) return;
            if (d is TextBlock block) ApplyTo(block, TextBlock.TextProperty, text);
            else if (d is Window window) ApplyTo(window, Window.TitleProperty, text);
            else if (d is TextBox box) ApplyTo(box, TextBox.TextProperty, text);
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

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (_pack == null || sender is not Window window) return;
            Watch(window, Window.TitleProperty);
        }

        private static void OnTextBoxLoaded(object sender, RoutedEventArgs e)
        {
            // Только поля для чтения: журналы и отчёты. То, что вводит человек, не трогаем.
            if (_pack == null || sender is not TextBox box || !box.IsReadOnly) return;
            Watch(box, TextBox.TextProperty);
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

        private static void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (_pack == null || sender is not Control control) return;
            TranslateProperty(control, AutomationProperties.NameProperty);
            TranslateProperty(control, AutomationProperties.HelpTextProperty);
            WatchSpokenName(control);
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

        private static void OnWatchedCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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
}
