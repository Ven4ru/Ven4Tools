using System;
using System.Threading;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Взаимное исключение долгих операций лаунчера: скачивание/обновление клиента,
    /// тихое автообновление из трея, установка клиента из локального архива, установка
    /// компонентов (winget / WebView2 / VC++ / Chocolatey).
    ///
    /// Почему именно взаимное исключение, а не по токену отмены на каждую операцию: все
    /// они делят один и тот же интерфейс — одну полосу прогресса, одну строку статуса,
    /// одну кнопку «Отмена», один индикатор этапа — и (для установок клиента) один
    /// каталог установки. Параллельно они не имеют смысла: две одновременные операции
    /// затирали бы прогресс друг друга, а «Отмена» физически не могла бы относиться
    /// сразу к обеим.
    ///
    /// Раньше на всё это было одно общее поле CancellationTokenSource, которое каждая
    /// операция присваивала, освобождала и обнуляла независимо от остальных. Вторая
    /// операция перезаписывала поле у ещё работающей первой и в своём finally делала
    /// Dispose токена, которым первая продолжала пользоваться, — дальнейший
    /// CreateLinkedTokenSource по этому токену давал ObjectDisposedException,
    /// всплывавший у пользователя как «❌ Ошибка скачивания» без объяснения причины.
    /// Заодно осиротевал исходный источник отмены, и кнопка «Отмена» с этого момента
    /// отменяла уже не ту операцию.
    ///
    /// Здесь слот один, и владеет им ровно одна операция: <see cref="TryBegin"/> либо
    /// выдаёт аренду (<see cref="OperationLease"/>), либо честно отказывает — и
    /// вызывающий код объясняет пользователю, что именно сейчас выполняется. Освободить
    /// слот может только сам владелец аренды, поэтому чужой токен отмены не может быть
    /// освобождён из-под работающей операции в принципе.
    ///
    /// Потокобезопасен: точки входа приходят и с UI-потока (клики), и из фоновой
    /// проверки обновлений.
    /// </summary>
    internal sealed class OperationGate
    {
        private readonly object _sync = new object();
        private OperationLease? _current;

        /// <summary>Занят ли слот. Пригодно только для подсказок в интерфейсе —
        /// решение о старте операции принимает атомарный <see cref="TryBegin"/>.</summary>
        internal bool IsBusy
        {
            get { lock (_sync) { return _current != null; } }
        }

        /// <summary>Название выполняющейся операции (для сообщения пользователю)
        /// либо null, если слот свободен.</summary>
        internal string? CurrentOperation
        {
            get { lock (_sync) { return _current?.Name; } }
        }

        /// <summary>
        /// Атомарно занять слот. Возвращает аренду с собственным токеном отмены либо
        /// null, если слот уже занят другой операцией.
        /// </summary>
        /// <param name="name">Человекочитаемое название — попадёт в журнал и в
        /// сообщение «Лаунчер занят» отклонённой операции.</param>
        /// <param name="timeout">Общий бюджет времени операции. Для сессий с
        /// модальными диалогами (установка компонентов) передаётся
        /// <see cref="Timeout.InfiniteTimeSpan"/>, а бюджет задаётся отдельно каждому
        /// шагу через <see cref="OperationLease.CreateStep"/>.</param>
        internal OperationLease? TryBegin(string name, TimeSpan timeout)
        {
            lock (_sync)
            {
                if (_current != null)
                    return null;

                var lease = new OperationLease(this, name, timeout);
                _current = lease;
                return lease;
            }
        }

        /// <summary>
        /// Отменить выполняющуюся операцию (кнопка «Отмена»). Возвращает false, если
        /// отменять нечего. Отменяется всегда именно текущая операция — не та, что
        /// завершилась ранее.
        /// </summary>
        internal bool CancelCurrent()
        {
            OperationLease? lease;
            // Cancel вызывается вне блокировки: отмена синхронно исполняет колбэки
            // подписчиков токена (обрыв HTTP-запроса и т.п.), и держать на это время
            // общий lock незачем.
            lock (_sync) { lease = _current; }
            if (lease == null)
                return false;

            lease.Cancel();
            return true;
        }

        /// <summary>
        /// Освободить слот. Ключевая проверка — освобождение только «своей» аренды:
        /// уже завершённая операция не должна вытеснять ту, что успела занять слот
        /// после неё (ровно этот сценарий и портил чужой токен раньше).
        /// </summary>
        internal void Release(OperationLease lease)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, lease))
                    _current = null;
            }
        }
    }

    /// <summary>
    /// Аренда слота операций: владеет источником отмены и освобождает слот при Dispose.
    /// Одноразовая — повторный Dispose безвреден и ничего не трогает.
    /// </summary>
    internal sealed class OperationLease : IDisposable
    {
        private readonly OperationGate _gate;
        private readonly CancellationTokenSource _cts;
        private volatile bool _finished;

        internal OperationLease(OperationGate gate, string name, TimeSpan timeout)
        {
            _gate = gate;
            Name  = name;
            _cts  = timeout == Timeout.InfiniteTimeSpan
                ? new CancellationTokenSource()
                : new CancellationTokenSource(timeout);
        }

        /// <summary>Человекочитаемое название операции.</summary>
        internal string Name { get; }

        /// <summary>Аренда ещё держит слот (операция не завершена).</summary>
        internal bool IsActive => !_finished;

        /// <summary>
        /// Токен отмены операции. Обращение после завершения аренды — ошибка в коде
        /// вызывающего, и она сообщается понятным сообщением, а не
        /// ObjectDisposedException из недр загрузчика.
        /// </summary>
        internal CancellationToken Token
        {
            get
            {
                ThrowIfFinished();
                return _cts.Token;
            }
        }

        /// <summary>
        /// Токен отдельного шага сессии: отменяется и по общей отмене операции, и по
        /// собственному таймауту шага. Нужен там, где одна операция состоит из
        /// нескольких установок со своими бюджетами времени (установка компонентов).
        /// Возвращённый источник освобождает вызывающий код (using).
        /// </summary>
        internal CancellationTokenSource CreateStep(TimeSpan timeout)
        {
            ThrowIfFinished();
            try
            {
                var step = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                if (timeout != Timeout.InfiniteTimeSpan)
                    step.CancelAfter(timeout);
                return step;
            }
            catch (ObjectDisposedException ex)
            {
                // Аренда завершилась между проверкой выше и связыванием токенов.
                // По построению недостижимо (освобождает аренду только её владелец,
                // последовательно после своей работы), но если однажды станет
                // достижимо — пусть это будет внятная ошибка, а не «Ошибка скачивания».
                throw new InvalidOperationException(FinishedMessage, ex);
            }
        }

        /// <summary>Отменить операцию. После завершения аренды — no-op.</summary>
        internal void Cancel()
        {
            if (_finished)
                return;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException)
            {
                // Операция завершилась ровно в момент нажатия «Отмена» — отменять уже
                // нечего, это штатная гонка кнопки с концом операции.
            }
        }

        public void Dispose()
        {
            if (_finished)
                return;
            _finished = true;
            // Сначала освобождаем слот, потом источник: между этими шагами
            // CancelCurrent() уже не найдёт эту аренду, а прилетевший впритык
            // Cancel() гасится проверкой _finished и перехватом выше.
            _gate.Release(this);
            _cts.Dispose();
        }

        private void ThrowIfFinished()
        {
            if (_finished)
                throw new InvalidOperationException(FinishedMessage);
        }

        private string FinishedMessage =>
            $"Операция «{Name}» уже завершена — её токен отмены больше недействителен";
    }
}
