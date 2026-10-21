using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Запрет сна на время операций и лимитное подключение (лаунчер 3.12).
/// </summary>
public sealed class LauncherSleepAndMeteredTests
{
    // --- Стоимость подключения ---

    [Theory]
    [InlineData(NetworkCostProbe.CostUnknown, false)]
    [InlineData(NetworkCostProbe.CostUnrestricted, false)]
    [InlineData(NetworkCostProbe.CostUnrestricted | NetworkCostProbe.CostCongested, false)]
    [InlineData(NetworkCostProbe.CostFixed, true)]
    [InlineData(NetworkCostProbe.CostFixed | NetworkCostProbe.CostApproachingDataLimit, true)]
    [InlineData(NetworkCostProbe.CostVariable, true)]
    [InlineData(NetworkCostProbe.CostUnrestricted | NetworkCostProbe.CostRoaming, true)]
    [InlineData(NetworkCostProbe.CostOverDataLimit, true)]
    public void IsMeteredCost_FixedVariableRoaming(uint cost, bool metered)
    {
        Assert.Equal(metered, NetworkCostProbe.IsMeteredCost(cost));
    }

    // --- Запрет сна ---

    private sealed class FakeExecutionState
    {
        public int Blocks;
        public int Releases;
        public readonly List<int> BlockThreads = new();
        public readonly List<int> ReleaseThreads = new();
        public readonly ManualResetEventSlim Released = new(false);

        public SleepBlockerCore Create() => new(
            () =>
            {
                lock (this) { Blocks++; BlockThreads.Add(Environment.CurrentManagedThreadId); }
                return true;
            },
            () =>
            {
                lock (this) { Releases++; ReleaseThreads.Add(Environment.CurrentManagedThreadId); }
                Released.Set();
            });
    }

    [Fact]
    public async Task Acquire_SetsAndReleasesOnTheSameHolderThread_EvenAcrossAwait()
    {
        var state = new FakeExecutionState();
        var core = state.Create();

        var lease = core.Acquire("тест");
        // Продолжение после await — на другом потоке пула: сброс всё равно должен
        // выполниться на потоке-держателе, а не на текущем.
        await Task.Yield();
        await Task.Run(() => lease.Dispose());

        Assert.True(state.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, state.Blocks);
        Assert.Equal(1, state.Releases);
        Assert.Equal(state.BlockThreads[0], state.ReleaseThreads[0]);
        Assert.Equal(0, core.ActiveCount);
    }

    [Fact]
    public void OverlappingOperations_ShareOneHolder()
    {
        var state = new FakeExecutionState();
        var core = state.Create();

        var first = core.Acquire("загрузка");
        var second = core.Acquire("перенос");
        Assert.Equal(2, core.ActiveCount);

        first.Dispose();
        Assert.Equal(0, state.Releases);   // вторая операция ещё идёт
        Assert.NotNull(core.HolderThread);

        second.Dispose();
        Assert.True(state.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, state.Blocks);
        Assert.Equal(1, state.Releases);
    }

    [Fact]
    public void DoubleDispose_DoesNotUnderflow()
    {
        var state = new FakeExecutionState();
        var core = state.Create();

        var lease = core.Acquire("x");
        var other = core.Acquire("y");
        lease.Dispose();
        lease.Dispose();   // повторный Dispose не должен снимать чужой запрет
        Assert.Equal(1, core.ActiveCount);

        other.Dispose();
        Assert.True(state.Released.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, core.ActiveCount);
    }

    [Fact]
    public void FailedBlock_IsNotReleased()
    {
        int releases = 0;
        using var started = new ManualResetEventSlim(false);
        var core = new SleepBlockerCore(() => { started.Set(); return false; }, () => Interlocked.Increment(ref releases));

        var lease = core.Acquire("x");
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        lease.Dispose();
        Thread.Sleep(100);

        Assert.Equal(0, releases);
    }

    // --- Флаги манифеста ---

    [Fact]
    public void FeatureFlags_AbsentFieldMeansDefaultOn()
    {
        try
        {
            LauncherFeatureFlags.Apply(new LauncherFlags { StallDetection = false });
            Assert.False(LauncherFeatureFlags.StallDetectionEnabled);

            // Поле убрали из манифеста — поведение по умолчанию (ВКЛ), а не «как было».
            LauncherFeatureFlags.Apply(new LauncherFlags());
            Assert.True(LauncherFeatureFlags.StallDetectionEnabled);

            LauncherFeatureFlags.Seed(false);
            LauncherFeatureFlags.Apply(null);
            Assert.True(LauncherFeatureFlags.StallDetectionEnabled);
        }
        finally
        {
            LauncherFeatureFlags.Seed(null);
        }
    }
}
