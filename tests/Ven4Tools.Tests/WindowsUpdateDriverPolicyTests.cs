using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

/// <summary>
/// Запрет драйверов через Windows Update: включение запоминает прежнее значение,
/// выключение возвращает его или удаляет. Реестр подменён.
/// </summary>
public sealed class WindowsUpdateDriverPolicyTests : IDisposable
{
    private sealed class FakeStore : IDriverPolicyStore
    {
        public bool Exists { get; set; }
        public int Value { get; set; }
        public bool Readable { get; set; } = true;

        public (bool Readable, bool Exists, int Value) Read() => (Readable, Exists, Value);
        public bool Write(int value) { Exists = true; Value = value; return true; }
        public bool Delete() { Exists = false; Value = 0; return true; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-drvpol-" + Guid.NewGuid().ToString("N"));
    private string PriorPath => Path.Combine(_dir, "wu_driver_policy.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ЗначенияНеБыло_ВключитьИВыключить_ЗначениеУдалено()
    {
        var store = new FakeStore();
        var policy = new WindowsUpdateDriverPolicy(PriorPath, store);

        Assert.False(policy.IsBlocked());
        Assert.True(policy.Block());
        Assert.True(policy.IsBlocked());
        Assert.Equal(1, store.Value);

        Assert.True(policy.Unblock());
        Assert.False(store.Exists);
        Assert.False(File.Exists(PriorPath));
    }

    [Fact]
    public void БылоНоль_ВыключениеВозвращаетНоль()
    {
        var store = new FakeStore { Exists = true, Value = 0 };
        var policy = new WindowsUpdateDriverPolicy(PriorPath, store);

        policy.Block();
        policy.Unblock();

        Assert.True(store.Exists);
        Assert.Equal(0, store.Value);
    }

    [Fact]
    public void ПовторноеВключение_НеПерезаписываетПрежнее()
    {
        var store = new FakeStore { Exists = true, Value = 0 };
        var policy = new WindowsUpdateDriverPolicy(PriorPath, store);

        policy.Block();
        store.Value = 5; // кто-то поменял значение между включениями
        policy.Block();
        policy.Unblock();

        Assert.Equal(0, store.Value);
    }

    [Fact]
    public void ЗапретБылДоНас_ВыключениеУдаляетЗначение()
    {
        var store = new FakeStore { Exists = true, Value = 1 };
        var policy = new WindowsUpdateDriverPolicy(PriorPath, store);

        Assert.True(policy.IsBlocked());
        Assert.True(policy.Unblock());
        Assert.False(store.Exists);
    }

    [Fact]
    public void РеестрНеЧитается_СостояниеНеизвестно_ВключениеНеДелается()
    {
        var store = new FakeStore { Readable = false };
        var policy = new WindowsUpdateDriverPolicy(PriorPath, store);

        Assert.Null(policy.IsBlocked());
        Assert.False(policy.Block());
        Assert.False(store.Exists);
    }

    [Theory]
    [InlineData(false, 0, DriverPolicyRestoreAction.Delete, 0)]
    [InlineData(true, 0, DriverPolicyRestoreAction.WritePrior, 0)]
    [InlineData(true, 2, DriverPolicyRestoreAction.WritePrior, 2)]
    [InlineData(true, 1, DriverPolicyRestoreAction.Delete, 0)]
    public void ПланОтката(bool existed, int value, DriverPolicyRestoreAction action, int expectedValue)
    {
        var plan = WindowsUpdateDriverPolicy.PlanRestore(new DriverPolicyPrior { Existed = existed, Value = value });
        Assert.Equal(action, plan.Action);
        Assert.Equal(expectedValue, plan.Value);
    }

    [Fact]
    public void ПланОтката_БезЗаписи_Удаление()
    {
        Assert.Equal(DriverPolicyRestoreAction.Delete, WindowsUpdateDriverPolicy.PlanRestore(null).Action);
    }
}
