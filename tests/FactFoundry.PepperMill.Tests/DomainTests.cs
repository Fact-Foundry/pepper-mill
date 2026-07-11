using FactFoundry.PepperMill.Services;

namespace FactFoundry.PepperMill.Tests;

public class EpochTests
{
    [Fact]
    public void Current_ReturnsMonthIdAndNextMonthBoundary()
    {
        var epoch = Epoch.Current(new DateTimeOffset(2026, 7, 15, 3, 30, 0, TimeSpan.Zero));

        Assert.Equal("2026-07", epoch.Id);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), epoch.RotatesAtUtc);
    }

    [Fact]
    public void Current_DecemberRollsToNextYear()
    {
        var epoch = Epoch.Current(new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero));

        Assert.Equal("2026-12", epoch.Id);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), epoch.RotatesAtUtc);
    }
}

public class PepperGeneratorTests
{
    [Fact]
    public void Generate_Produces32UniqueBytes()
    {
        var a = PepperGenerator.Generate();
        var b = PepperGenerator.Generate();

        Assert.Equal(32, a.Length);
        Assert.Equal(32, b.Length);
        Assert.NotEqual(a, b);
    }
}
