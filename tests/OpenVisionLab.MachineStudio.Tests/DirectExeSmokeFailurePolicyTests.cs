using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class DirectExeSmokeFailurePolicyTests
{
    [Fact]
    public void SelectExitCodeReturnsZeroWhenAllChecksAreValid()
    {
        var exitCode = DirectExeSmokeFailurePolicy.SelectExitCode(
            new DirectExeSmokeFailureCheck(true, 3),
            new DirectExeSmokeFailureCheck(true, 4));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void SelectExitCodeReturnsFirstInvalidCodeInOrder()
    {
        var exitCode = DirectExeSmokeFailurePolicy.SelectExitCode(
            new DirectExeSmokeFailureCheck(false, 3),
            new DirectExeSmokeFailureCheck(false, 4));

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void SelectExitCodeSkipsValidChecksBeforeLaterFailure()
    {
        var exitCode = DirectExeSmokeFailurePolicy.SelectExitCode(
            new DirectExeSmokeFailureCheck(true, 3),
            new DirectExeSmokeFailureCheck(false, 4),
            new DirectExeSmokeFailureCheck(false, 5));

        Assert.Equal(4, exitCode);
    }
}
