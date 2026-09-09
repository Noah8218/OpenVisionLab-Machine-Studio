using OpenVisionLab.Machine.Simulation.Compilation;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class FixedStepDelayConverterTests
{
    [Fact]
    public void Constructor_RejectsNonPositiveFixedStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedStepDelayConverter(TimeSpan.Zero));
    }

    [Fact]
    public void TryConvertDelayToTicks_ConvertsExactMultiple()
    {
        var converter = new FixedStepDelayConverter(TimeSpan.FromMilliseconds(5));

        bool converted = converter.TryConvertDelayToTicks(15, allowZero: false, out int ticks);

        Assert.True(converted);
        Assert.Equal(3, ticks);
    }

    [Fact]
    public void TryConvertDelayToTicks_RejectsUnalignedDelay()
    {
        var converter = new FixedStepDelayConverter(TimeSpan.FromMilliseconds(5));

        bool converted = converter.TryConvertDelayToTicks(7, allowZero: true, out int ticks);

        Assert.False(converted);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void TryConvertDelayToTicks_RespectsZeroPolicy()
    {
        var converter = new FixedStepDelayConverter(TimeSpan.FromMilliseconds(5));

        Assert.False(converter.TryConvertDelayToTicks(0, allowZero: false, out _));
        Assert.True(converter.TryConvertDelayToTicks(0, allowZero: true, out int ticks));
        Assert.Equal(0, ticks);
    }
}
