using System;
using System.Collections.Generic;
using System.IO;

namespace Ven4Tools.Helpers
{
    /// <summary>
    /// Сбой WPF «шрифт зарегистрирован в системе, а его файл недоступен».
    /// <para>Отчёт id 6 (клиент 6.5.1, Windows 10.0.26300): <see cref="FileNotFoundException"/>
    /// из <c>GlyphTypeface.GetGlyphMetricsOptimized</c> при разметке строки списка — DirectWrite
    /// не смог открыть файл шрифта (основного или запасного для отдельного символа). Причина
    /// в системе пользователя (удалённый или недоступный файл шрифта, битый кэш шрифтов), а не
    /// в клиенте, но до этой правки она закрывала программу целиком через
    /// <c>DispatcherUnhandledException</c>.</para>
    /// <para>Такой сбой гасится и пишется в журнал. Если он повторяется на каждом проходе
    /// разметки, программа всё равно не работала бы — тогда, как и раньше, контролируемое
    /// завершение: больше <see cref="MaxFailures"/> сбоев за <see cref="Window"/> подряд не гасим.</para>
    /// </summary>
    internal sealed class FontFileFailureGuard
    {
        internal const int MaxFailures = 10;
        internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

        private readonly Func<DateTime> _now;
        private readonly Queue<DateTime> _recent = new();
        private readonly object _lock = new();

        public FontFileFailureGuard(Func<DateTime>? now = null)
        {
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>Исключение — недоступный файл шрифта при разметке текста WPF.</summary>
        public static bool IsMissingFontFile(Exception ex)
        {
            if (ex is not FileNotFoundException) return false;
            string stack = ex.StackTrace ?? "";
            return stack.Contains("System.Windows.Media.GlyphTypeface", StringComparison.Ordinal)
                || stack.Contains("MS.Internal.Text.TextInterface", StringComparison.Ordinal);
        }

        /// <summary>
        /// true — сбой шрифта можно погасить и продолжить работу; false — это не он
        /// или он идёт непрерывно, и обработчик должен завершить программу как прежде.
        /// </summary>
        public bool TrySuppress(Exception ex)
        {
            if (!IsMissingFontFile(ex)) return false;

            lock (_lock)
            {
                DateTime now = _now();
                while (_recent.Count > 0 && now - _recent.Peek() > Window)
                    _recent.Dequeue();

                if (_recent.Count >= MaxFailures) return false;
                _recent.Enqueue(now);
                return true;
            }
        }
    }
}
