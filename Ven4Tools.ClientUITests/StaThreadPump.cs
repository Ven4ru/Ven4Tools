using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// UI Automation (FlaUI/UIA3) — COM-based API, рассчитанный на создание и
    /// использование с одного STA-потока с активным насосом сообщений. VSTest
    /// по умолчанию выполняет тестовые методы на MTA-потоке — тот же класс
    /// проблемы, что уже задокументирован в Ven4Tools/Services/AppLaunchResolver.cs
    /// для WScript.Shell (COMException либо тихий сбой маршалинга через
    /// STA-прокси). На практике проявлялось как недетерминированный частичный/
    /// пустой результат FindFirstDescendant/FindAllDescendants по большому
    /// дереву каталога (0 или случайное подмножество из 71 приложения), хотя
    /// тот же запрос со честного STA-потока с насосом отрабатывал стабильно.
    ///
    /// Даёт один персистентный STA-поток на весь жизненный цикл тестового
    /// класса — AppSession.Launch() и каждый последующий FlaUI-вызов идут
    /// с одного и того же корректно накачиваемого потока.
    /// </summary>
    internal sealed class StaThreadPump : IDisposable
    {
        private readonly Thread _thread;
        private readonly BlockingCollection<Action> _queue = new();

        public StaThreadPump()
        {
            _thread = new Thread(RunLoop) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void RunLoop()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }
        }

        public T Invoke<T>(Func<T> func)
        {
            T result = default!;
            Exception? captured = null;
            using var done = new ManualResetEventSlim(false);
            _queue.Add(() =>
            {
                try { result = func(); }
                catch (Exception ex) { captured = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (captured != null)
                ExceptionDispatchInfo.Capture(captured).Throw();
            return result;
        }

        public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
