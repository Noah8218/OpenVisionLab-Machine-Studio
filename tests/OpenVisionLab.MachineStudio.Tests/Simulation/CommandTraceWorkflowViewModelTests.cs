using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class CommandTraceWorkflowViewModelTests
{
    private static string SamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "AutomaticTransferCell.ovmachine");

    private static string EvidenceRoot =>
        "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\pl-0027-command-trace-ui\\viewmodel";

    [Fact]
    public async Task ExplicitCaptureExportsOnlyLaterCommandsAndReplaysWithoutProjectMutation()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Axes.Count > 0);

        viewModel.IsRunMode = true;
        Assert.True(viewModel.CanStartSimulationCommandTraceCapture);

        viewModel.StartSimulationCommandTraceCaptureCommand.Execute(null);
        Assert.Equal(0, viewModel.SimulationCommandTraceEntryCount);

        viewModel.ResetCommand.Execute(null);
        await WaitForAsync(() => viewModel.SimulationCommandTraceEntryCount == 1);

        Directory.CreateDirectory(EvidenceRoot);
        var tracePath = Path.Combine(EvidenceRoot, "viewmodel-roundtrip.ovsim-trace.json");
        Assert.True(viewModel.TryExportSimulationCommandTrace(tracePath));

        var package = DeterministicSimulationCommandTracePackage.LoadFromJson(tracePath);
        Assert.NotNull(package);
        Assert.True(package!.CanReplay);
        Assert.Single(package.Entries);
        Assert.Equal(nameof(OpenVisionLab.Machine.Simulation.Commands.ResetCommand), package.Entries[0].CommandType);
        Assert.False(viewModel.HasUnsavedChanges);

        Assert.True(await viewModel.TryReplaySimulationCommandTraceAsync(tracePath));
        Assert.True(viewModel.LastSimulationCommandTraceReplaySucceeded);
        Assert.False(viewModel.IsRunning);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task InvalidTraceIsRejectedBeforeRuntimeMutation()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        using var viewModel = new MainViewModel(project);
        await WaitForAsync(() => viewModel.SceneSnapshots.Latest?.Axes.Count > 0);
        viewModel.IsRunMode = true;

        Directory.CreateDirectory(EvidenceRoot);
        var tracePath = Path.Combine(EvidenceRoot, "viewmodel-invalid.ovsim-trace.json");
        await File.WriteAllTextAsync(tracePath, "{\"schemaVersion\":1,\"traceHash\":\"tampered\"}");
        var beforeTick = viewModel.SceneSnapshots.Latest?.TickIndex;
        var beforeDirty = viewModel.HasUnsavedChanges;

        Assert.False(await viewModel.TryReplaySimulationCommandTraceAsync(tracePath));
        Assert.False(viewModel.LastSimulationCommandTraceReplaySucceeded);
        Assert.Equal(beforeTick, viewModel.SceneSnapshots.Latest?.TickIndex);
        Assert.Equal(beforeDirty, viewModel.HasUnsavedChanges);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The deterministic runtime did not become observable.");
    }
}
