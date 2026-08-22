using OpenVisionLab.Machine.Core.Axes;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public class VirtualAxisDefinitionTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var axis = new VirtualAxisDefinition();
        Assert.Equal(AxisKind.Linear, axis.Kind);
        Assert.Equal("mm", axis.Unit);
        Assert.Equal(VirtualAxisDefinition.DefaultMaxVelocity, axis.MaxVelocity);
        Assert.Equal(VirtualAxisDefinition.DefaultMaxAcceleration, axis.MaxAcceleration);
        Assert.Null(axis.MaxDeceleration);
        Assert.Null(axis.FollowingErrorLimit);
    }
}
