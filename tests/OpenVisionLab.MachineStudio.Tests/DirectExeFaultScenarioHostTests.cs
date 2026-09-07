using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class DirectExeFaultScenarioHostTests
{
    [Fact]
    public async Task RunAsyncReturnsArgumentErrorWhenProjectIsMissing()
    {
        var exitCode = await DirectExeFaultScenarioHost.RunAsync(
            ["--fault-scenario", "scenario.json"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsyncReturnsArgumentErrorWhenScenarioIsMissing()
    {
        var exitCode = await DirectExeFaultScenarioHost.RunAsync(
            ["--fault-project", "project.ovmachine"]);

        Assert.Equal(2, exitCode);
    }
}
