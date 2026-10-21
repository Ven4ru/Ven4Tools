using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Не даёт компьютеру уйти в сон, пока идёт установка, обновление, перенос или
    /// восстановление клиента: уснувший посреди загрузки ПК после пробуждения получает
    /// оборванное соединение, а посреди переноса — недоделанную копию.
    ///
    /// SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) действует, пока его
    /// не сбросит ТОТ ЖЕ поток или пока этот поток не завершится. Операции лаунчера
    /// асинхронные: продолжение после await приходит на другой поток пула, и сброс из
    /// finally ушёл бы не туда — запрет сна остался бы висеть на чужом потоке до
    /// конца процесса. Поэтому состояние держит отдельный поток-держатель: он ставит
    /// запрет, ждёт сигнала и сам же его снимает. Даже если процесс убьют, вместе с
    /// потоком исчезнет и запрет — «забыть» снять его некому.
    ///
    /// Запреты считаются: пока жив хотя бы один <see cref="Acquire"/>, держатель один.
    /// Экран при этом гаснуть может (ES_DISPLAY_REQUIRED не ставится) — нужен только
    /// работающий компьютер, а не включённый монитор.
    /// </summary>
    internal static class SleepBlocker
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        private static readonly SleepBlockerCore Core = new(
            () => SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0,
            () => SetThreadExecutionState(EsContinuous),
            message => LauncherLog.Write(message));

        /// <summary>
        /// Запретить сон до Dispose возвращённого объекта. Повторный Dispose безопасен.
        /// </summary>
        public static IDisposable Acquire(string reason) => Core.Acquire(reason);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
    }

    /// <summary>
    /// Счётчик запретов и поток-держатель — без P/Invoke, чтобы логику (порядок
    /// установки/снятия, повторный Dispose, перекрывающиеся операции) можно было
    /// проверить unit-тестами.
    /// </summary>
    internal sealed class SleepBlockerCore
    {
        private readonly Func<bool> _block;
        private readonly Action _release;
        private readonly Action<string>? _log;
        private readonly object _sync = new();
        private int _count;
        private ManualResetEventSlim? _stopSignal;
        private Thread? _holder;

        public SleepBlockerCore(Func<bool> block, Action release, Action<string>? log = null)
        {
            _block = block;
            _release = release;
            _log = log;
        }

        /// <summary>Сколько запретов сейчас активно (для тестов).</summary>
        internal int ActiveCount
        {
            get { lock (_sync) return _count; }
        }

        /// <summary>Поток-держатель текущего запрета (для тестов) или null.</summary>
        internal Thread? HolderThread
        {
            get { lock (_sync) return _holder; }
        }

        public IDisposable Acquire(string reason)
        {
            lock (_sync)
            {
                _count++;
                if (_count == 1) StartHolder(reason);
            }
            return new Lease(this);
        }

        private void Release()
        {
            lock (_sync)
            {
                if (_count == 0) return;
                _count--;
                if (_count > 0) return;

                // Сигнал держателю: он сам снимет запрет на своём потоке и завершится.
                // Ждать его здесь незачем — следующий Acquire заведёт новый держатель,
                // а запреты двух потоков Windows складывает независимо.
                _stopSignal?.Set();
                _stopSignal = null;
                _holder = null;
            }
        }

        private void StartHolder(string reason)
        {
            var stop = new ManualResetEventSlim(false);
            var started = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                bool blocked = false;
                try
                {
                    blocked = _block();
                    _log?.Invoke(blocked
                        ? $"☕ Сон компьютера запрещён на время операции: {reason}"
                        : $"⚠️ Не удалось запретить сон компьютера на время операции: {reason}");
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"⚠️ Не удалось запретить сон компьютера: {ex.Message}");
                }
                finally
                {
                    started.Set();
                }

                stop.Wait();
                try
                {
                    if (blocked)
                    {
                        _release();
                        _log?.Invoke("☕ Запрет сна снят");
                    }
                }
                catch { /* поток всё равно завершается, а с ним уходит и запрет */ }
                finally
                {
                    stop.Dispose();
                    started.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "Ven4Tools.SleepBlocker",
            };

            _stopSignal = stop;
            _holder = thread;
            thread.Start();
            // Операция начинается только после того, как запрет действительно поставлен.
            // Под замком — 1 мс работы держателя; второй Acquire в это время просто ждёт.
            started.Wait(TimeSpan.FromSeconds(2));
        }

        private sealed class Lease : IDisposable
        {
            private SleepBlockerCore? _owner;

            public Lease(SleepBlockerCore owner) => _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
