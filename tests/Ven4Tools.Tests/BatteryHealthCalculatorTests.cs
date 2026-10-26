using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class BatteryHealthCalculatorTests
{
    [Fact]
    public void Normal_PercentOfDesign()
    {
        var r = BatteryHealthCalculator.Evaluate(new BatteryRaw(50_000, 41_000, 312));
        Assert.True(r.HasData);
        Assert.Equal(82, r.Percent);
        Assert.Equal(312, r.Cycles);
        Assert.StartsWith("Ёмкость: 82% от заводской", r.Text);
    }

    [Theory]
    [InlineData(0L, 40_000L)]
    [InlineData(50_000L, 0L)]
    [InlineData(null, 40_000L)]
    [InlineData(50_000L, null)]
    [InlineData(10_000L, 40_000L)]   // текущая больше заводской в разы — мусор
    [InlineData(5_000_000L, 40_000L)] // заводская в 5 кВт·ч — мусор
    public void Garbage_IsNoData(long? designed, long? full)
    {
        var r = BatteryHealthCalculator.Evaluate(new BatteryRaw(designed, full, 100));
        Assert.False(r.HasData);
        Assert.Null(r.Percent);
        Assert.Contains("нет данных", r.Text);
    }

    [Fact]
    public void SlightlyAboveDesign_IsAccepted()
    {
        var r = BatteryHealthCalculator.Evaluate(new BatteryRaw(50_000, 52_000, 3));
        Assert.True(r.HasData);
        Assert.Equal(104, r.Percent);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(null)]
    [InlineData(999_999L)]
    public void ZeroOrGarbageCycles_AreNoData(long? cycles)
    {
        var r = BatteryHealthCalculator.Evaluate(new BatteryRaw(50_000, 45_000, cycles));
        Assert.True(r.HasData);
        Assert.Null(r.Cycles);
        Assert.EndsWith("циклов: нет данных", r.Text);
    }

    [Fact]
    public void Combine_SumsCapacities_TakesMaxCycles()
    {
        var c = BatteryHealthCalculator.Combine(new[]
        {
            new BatteryRaw(40_000, 30_000, 100),
            new BatteryRaw(20_000, 18_000, 250)
        });
        Assert.Equal(60_000, c.DesignedMwh);
        Assert.Equal(48_000, c.FullChargedMwh);
        Assert.Equal(250, c.Cycles);
    }

    [Fact]
    public void Combine_OneBatteryWithoutData_GivesNoCapacity()
    {
        var c = BatteryHealthCalculator.Combine(new[] { new BatteryRaw(40_000, 30_000, 1), new BatteryRaw(null, 18_000, 2) });
        Assert.Null(c.DesignedMwh);
    }

    [Fact]
    public void ParsePowercfgXml_ReadsBatteriesByLocalName()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <Id>DELL 1</Id>
                  <DesignCapacity>56000</DesignCapacity>
                  <FullChargeCapacity>47040</FullChargeCapacity>
                  <CycleCount>412</CycleCount>
                </Battery>
              </Batteries>
            </BatteryReport>
            """;
        var list = BatteryHealthCalculator.ParsePowercfgXml(xml);
        var b = Assert.Single(list);
        Assert.Equal(56000, b.DesignedMwh);
        Assert.Equal(47040, b.FullChargedMwh);
        Assert.Equal(412, b.Cycles);
        Assert.Equal(84, BatteryHealthCalculator.Evaluate(b).Percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не xml")]
    [InlineData("<BatteryReport><Batteries/></BatteryReport>")]
    public void ParsePowercfgXml_BadOrEmpty_GivesNothing(string xml)
    {
        Assert.Empty(BatteryHealthCalculator.ParsePowercfgXml(xml));
    }
}
