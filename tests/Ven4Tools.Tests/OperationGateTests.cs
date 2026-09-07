using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Слот долгих операций лаунчера. Раньше на его месте было одно общее поле
/// CancellationTokenSource, которое пять точек входа присваивали и освобождали
/// независимо друг от друга: вторая операция диспоузила токен ещё работающей первой
/// (ObjectDisposedException, всплывавший как «Ошибка скачивания»), осиротевший
/// источник отмены оставался никому не подконтрольным, а кнопка «Отмена» с этого
/// момента отменяла уже не ту операцию. Тесты закрывают именно эти инварианты.
/// </summary>
public sealed class OperationGateTests
{
    [Fact]
    public void TryBegin_ЗанимаетСвободныйСлот()
    {
        var gate = new OperationGate();

        Assert.False(gate.IsBusy);
        Assert.Null(gate.CurrentOperation);

        using var lease = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);

        Assert.NotNull(lease);
        Assert.True(gate.IsBusy);
        Assert.Equal("Загрузка клиента", gate.CurrentOperation);
        Assert.True(lease!.IsActive);
    }

    [Fact]
    public void TryBegin_ОтклоняетВторуюОперацию_ПокаПерваяРаботает()
    {
        var gate = new OperationGate();
        using var first = gate.TryBegin("Автоматическое обновление клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(first);

        var second = gate.TryBegin("Устранение проблем с компонентами", Timeout.InfiniteTimeSpan);

        // Отказ, а не молчаливый перехват — и вызывающий код знает, ЧТО именно занято.
        Assert.Null(second);
        Assert.Equal("Автоматическое обновление клиента", gate.CurrentOperation);
    }

    [Fact]
    public void ОтклонённаяОперация_НеПортитТокенРаботающей()
    {
        // Прямая регрессия исходного бага: фоновое автообновление держит токен, поверх
        // него запускается установка компонентов. Раньше вторая операция перезаписывала
        // общее поле и в своём finally освобождала чужой источник — дальнейший
        // CreateLinkedTokenSource по осиротевшему токену давал ObjectDisposedException.
        var gate = new OperationGate();
        using var background = gate.TryBegin("Автоматическое обновление клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(background);
        var backgroundToken = background!.Token;

        Assert.Null(gate.TryBegin("Устранение проблем с компонентами", Timeout.InfiniteTimeSpan));

        // Токен фоновой операции жив: не отменён и пригоден для связывания —
        // ровно то, что делает FallbackDownloader.DownloadSingleAsync.
        Assert.False(backgroundToken.IsCancellationRequested);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(backgroundToken);
        Assert.False(linked.Token.IsCancellationRequested);
        Assert.True(background.IsActive);
    }

    [Fact]
    public void Dispose_ОсвобождаетСлотДляСледующейОперации()
    {
        var gate = new OperationGate();

        var first = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(first);
        first!.Dispose();

        Assert.False(gate.IsBusy);
        Assert.Null(gate.CurrentOperation);

        using var second = gate.TryBegin("Установка клиента из файла", Timeout.InfiniteTimeSpan);
        Assert.NotNull(second);
        Assert.Equal("Установка клиента из файла", gate.CurrentOperation);
    }

    [Fact]
    public void ЗавершённаяАренда_НеВытесняетСледующую()
    {
        // Сердцевина защиты от «осиротевшей» операции: повторный (в т.ч. запоздалый)
        // Dispose первой аренды не должен обнулять слот, который уже принадлежит второй.
        var gate = new OperationGate();

        var first = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = gate.TryBegin("Установка компонентов из setup", Timeout.InfiniteTimeSpan);
        Assert.NotNull(second);

        first.Dispose(); // запоздалый finally уже завершённой операции

        Assert.True(gate.IsBusy);
        Assert.Equal("Установка компонентов из setup", gate.CurrentOperation);
        Assert.True(second!.IsActive);
        Assert.False(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void CancelCurrent_ОтменяетИменноТекущуюОперацию()
    {
        var gate = new OperationGate();

        // Отменять нечего — кнопка «Отмена» не должна делать вид, что что-то отменила.
        Assert.False(gate.CancelCurrent());

        var first = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(first);
        var firstToken = first!.Token;
        first.Dispose();

        using var second = gate.TryBegin("Установка клиента из файла", Timeout.InfiniteTimeSpan);
        Assert.NotNull(second);
        var secondToken = second!.Token;

        Assert.True(gate.CancelCurrent());

        // Раньше «Отмена» после смены операции била по полю, которое уже принадлежало
        // не тому, кого видел пользователь.
        Assert.True(secondToken.IsCancellationRequested);
        Assert.False(firstToken.IsCancellationRequested);
    }

    [Fact]
    public void CancelCurrent_ПослеЗавершенияОперации_НеБросаетИсключение()
    {
        var gate = new OperationGate();

        var lease = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(lease);
        lease!.Dispose();

        // Штатная гонка: пользователь жмёт «Отмена» ровно в момент, когда операция
        // закончилась. Ни исключения, ни ложного «отменено».
        Assert.False(gate.CancelCurrent());
        lease.Cancel();
    }

    [Fact]
    public void CreateStep_СвязанСАрендой_ОтменяетсяВместеСНей()
    {
        var gate = new OperationGate();
        using var lease = gate.TryBegin("Устранение проблем с компонентами", Timeout.InfiniteTimeSpan);
        Assert.NotNull(lease);

        using var step = lease!.CreateStep(TimeSpan.FromMinutes(10));
        Assert.False(step.Token.IsCancellationRequested);

        // Кнопка «Отмена» гасит сессию — вместе с ней должен прерваться и текущий шаг
        // (скачивание/установка winget, WebView2, VC++, Chocolatey).
        Assert.True(gate.CancelCurrent());
        Assert.True(step.Token.IsCancellationRequested);
        Assert.True(lease.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CreateStep_СобственныйТаймаут_НеОтменяетВсюСессию()
    {
        var gate = new OperationGate();
        using var lease = gate.TryBegin("Устранение проблем с компонентами", Timeout.InfiniteTimeSpan);
        Assert.NotNull(lease);

        using (var step = lease!.CreateStep(TimeSpan.FromMilliseconds(30)))
        {
            while (!step.Token.IsCancellationRequested)
                await Task.Delay(10);

            Assert.True(step.Token.IsCancellationRequested);
        }

        // Сессия из нескольких установок переживает таймаут одного шага: следующий
        // компонент должен ставиться дальше.
        Assert.False(lease.Token.IsCancellationRequested);
        using var next = lease.CreateStep(TimeSpan.FromMinutes(5));
        Assert.False(next.Token.IsCancellationRequested);
    }

    [Fact]
    public void ЗавершённаяАренда_ДаётПонятнуюОшибку_АНеObjectDisposedException()
    {
        var gate = new OperationGate();
        var lease = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);
        Assert.NotNull(lease);
        lease!.Dispose();

        Assert.False(lease.IsActive);

        // Обращение к токену завершённой операции — ошибка в коде вызывающего. Она
        // должна быть внятной, а не ObjectDisposedException из недр загрузчика,
        // который пользователь видел как «❌ Ошибка скачивания».
        var tokenEx = Assert.Throws<InvalidOperationException>(() => lease.Token);
        Assert.Contains("Загрузка клиента", tokenEx.Message);
        Assert.Contains("уже завершена", tokenEx.Message);

        var stepEx = Assert.Throws<InvalidOperationException>(
            () => lease.CreateStep(TimeSpan.FromMinutes(1)));
        Assert.Contains("Загрузка клиента", stepEx.Message);
    }

    [Fact]
    public void АрендаСТаймаутом_ОтменяетсяСамаПоИстечении()
    {
        var gate = new OperationGate();
        using var lease = gate.TryBegin("Загрузка клиента", TimeSpan.FromMilliseconds(1));
        Assert.NotNull(lease);

        // Бюджет времени операции (в бою — 30 минут на скачивание клиента) страхует от
        // подвисшего, но не оборванного соединения.
        SpinWait.SpinUntil(() => lease!.Token.IsCancellationRequested, TimeSpan.FromSeconds(5));
        Assert.True(lease!.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ПараллельныеПопытки_ЗанимаютСлотРовноОдинРаз()
    {
        // Точки входа приходят с разных потоков: клики с UI-потока, тихое
        // автообновление — из фоновой проверки. Проверка «свободно?» и захват обязаны
        // быть неделимыми, иначе возвращается исходная гонка.
        var gate = new OperationGate();
        const int Racers = 32;

        using var start = new SemaphoreSlim(0, Racers);
        var attempts = new Task<OperationLease?>[Racers];
        for (int i = 0; i < Racers; i++)
        {
            int index = i;
            attempts[i] = Task.Run(async () =>
            {
                await start.WaitAsync();
                return gate.TryBegin($"Операция {index}", Timeout.InfiniteTimeSpan);
            });
        }

        start.Release(Racers);
        var leases = await Task.WhenAll(attempts);

        var winners = leases.Where(l => l != null).ToList();
        Assert.Single(winners);
        Assert.Equal(winners[0]!.Name, gate.CurrentOperation);

        winners[0]!.Dispose();
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task ПараллельныеОсвобождениеИОтмена_НеБросаютИсключений()
    {
        // Кнопка «Отмена» с UI-потока против finally операции, завершающейся на потоке
        // пула. Ни одна из сторон не должна получить ObjectDisposedException.
        var gate = new OperationGate();

        for (int i = 0; i < 200; i++)
        {
            var lease = gate.TryBegin("Загрузка клиента", Timeout.InfiniteTimeSpan);
            Assert.NotNull(lease);

            var cancel  = Task.Run(() => gate.CancelCurrent());
            var release = Task.Run(() => lease!.Dispose());
            await Task.WhenAll(cancel, release);

            Assert.False(gate.IsBusy);
        }
    }
}
