using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RecipeConnectionSetupWorkflowTests
{
    [Fact]
    public void LoadLockSetupMapsApplyAndNoChangeOutcomesToShellCallbacks()
    {
        var project = new MachineProjectDocument { Name = "Load lock workflow test" };
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var completed = 0;
        var workflow = CreateWorkflow(
            project,
            statuses,
            logs,
            () => completed++);
        var setup = new LoadLockDefinition
        {
            OuterDoorComponentId = "outer-door",
            InnerDoorComponentId = "inner-door",
            EvacuateCommandChannelId = "do-evacuate",
            VentCommandChannelId = "do-vent",
            VacuumReadySensorChannelId = "di-vacuum-ready",
            AtmosphereReadySensorChannelId = "di-atmosphere-ready"
        };

        Assert.Equal(1, workflow.ApplyLoadLockSetup(setup));
        Assert.Equal(1, completed);
        Assert.Single(logs);
        Assert.Equal("Project", logs[0].Category);
        Assert.Contains("load-lock-1", logs[0].Message, StringComparison.Ordinal);

        Assert.Equal(0, workflow.ApplyLoadLockSetup(setup));
        Assert.Equal(1, completed);
        Assert.Equal(2, statuses.Count);
    }

    [Fact]
    public void MultipleLoadLocksAreRejectedWithoutCompletionOrProjectMutation()
    {
        var project = new MachineProjectDocument { Name = "Multiple load locks workflow test" };
        project.Devices.Add(new DeviceDefinition { Id = "load-lock-1", Kind = DeviceKind.LoadLock });
        project.Devices.Add(new DeviceDefinition { Id = "load-lock-2", Kind = DeviceKind.LoadLock });
        var before = new ProjectDocumentStore().SerializeForEvidence(project);
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var completed = 0;
        var workflow = CreateWorkflow(
            project,
            statuses,
            logs,
            () => completed++);

        Assert.Equal(
            0,
            workflow.ApplyLoadLockSetup(new LoadLockDefinition { OuterDoorComponentId = "outer-door" }));

        Assert.Equal(0, completed);
        Assert.Empty(logs);
        Assert.Single(statuses);
        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
    }

    [Fact]
    public void ProcessBlockSetupUsesProcessCompletionAndSequenceLog()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "10-MetrologySorter.ovmachine")));
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var setupCompleted = 0;
        var processCompleted = 0;
        var workflow = CreateWorkflow(
            project,
            statuses,
            logs,
            () => setupCompleted++,
            () => processCompleted++);

        var changeCount = workflow.ApplyProcessBlocks(Enum.GetValues<SemiconductorProcessBlockKind>());

        Assert.True(changeCount > 0);
        Assert.Equal(0, setupCompleted);
        Assert.Equal(1, processCompleted);
        var log = Assert.Single(logs);
        Assert.Equal("Sequence", log.Category);
        Assert.Contains("process plan", log.Message, StringComparison.Ordinal);
        Assert.Single(statuses);
    }

    private static RecipeConnectionSetupWorkflow CreateWorkflow(
        MachineProjectDocument project,
        List<string> statuses,
        List<(string Category, string Message)> logs,
        Action completeSetupMutation,
        Action? completeProcessBlockMutation = null) =>
        new(
            new RecipeConnectionProjectApplier(),
            () => project,
            () => { },
            completeSetupMutation,
            completeProcessBlockMutation ?? ThrowUnexpectedProcessBlockCompletion,
            statuses.Add,
            (category, message) => logs.Add((category, message)));

    private static void ThrowUnexpectedProcessBlockCompletion() =>
        throw new InvalidOperationException("Process-block completion is not expected in this test.");
}
