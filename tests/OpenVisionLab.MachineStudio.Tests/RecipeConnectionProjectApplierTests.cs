using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RecipeConnectionProjectApplierTests
{
    private readonly RecipeConnectionProjectApplier _applier = new();

    [Fact]
    public void LoadLockSetupCreatesDeterministicDeviceAndSecondApplyIsNoOp()
    {
        var project = new MachineProjectDocument { Name = "Load lock applier test" };
        var setup = new LoadLockDefinition
        {
            OuterDoorComponentId = "outer-door",
            InnerDoorComponentId = "inner-door",
            EvacuateCommandChannelId = "do-evacuate",
            VentCommandChannelId = "do-vent",
            VacuumReadySensorChannelId = "di-vacuum-ready",
            AtmosphereReadySensorChannelId = "di-atmosphere-ready",
            PumpDownDurationMilliseconds = 700,
            VentDurationMilliseconds = 800
        };

        var applied = _applier.ApplyLoadLockSetup(project, setup);

        Assert.Equal(RecipeConnectionProjectApplyOutcome.Applied, applied.Outcome);
        Assert.Equal(1, applied.ChangeCount);
        var device = Assert.Single(project.Devices);
        Assert.Equal("load-lock-1", device.Id);
        Assert.Equal(DeviceKind.LoadLock, device.Kind);
        Assert.Equal(
            new[]
            {
                "do-evacuate",
                "do-vent",
                "di-vacuum-ready",
                "di-atmosphere-ready"
            },
            device.ChannelIds);
        Assert.Equal(setup.PumpDownDurationMilliseconds, device.LoadLock?.PumpDownDurationMilliseconds);
        Assert.Equal(setup.VentDurationMilliseconds, device.LoadLock?.VentDurationMilliseconds);

        var repeated = _applier.ApplyLoadLockSetup(project, setup);

        Assert.Equal(RecipeConnectionProjectApplyOutcome.NoChanges, repeated.Outcome);
        Assert.Equal(0, repeated.ChangeCount);
        Assert.Single(project.Devices);
    }

    [Fact]
    public void MultipleLoadLocksAreRejectedWithoutProjectMutation()
    {
        var project = new MachineProjectDocument { Name = "Multiple load locks" };
        project.Devices.Add(new DeviceDefinition { Id = "load-lock-1", Kind = DeviceKind.LoadLock });
        project.Devices.Add(new DeviceDefinition { Id = "load-lock-2", Kind = DeviceKind.LoadLock });
        var before = new ProjectDocumentStore().SerializeForEvidence(project);

        var result = _applier.ApplyLoadLockSetup(
            project,
            new LoadLockDefinition { OuterDoorComponentId = "outer" });

        Assert.Equal(RecipeConnectionProjectApplyOutcome.MultipleDevices, result.Outcome);
        Assert.Equal(before, new ProjectDocumentStore().SerializeForEvidence(project));
    }

    [Fact]
    public void ProcessBlockApplyForwardsMutationCountsAndBecomesNoOpWhenRepeated()
    {
        var store = new ProjectDocumentStore();
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "10-MetrologySorter.ovmachine");
        var project = store.Load(File.ReadAllText(path));
        var kinds = Enum.GetValues<SemiconductorProcessBlockKind>();

        var applied = _applier.ApplyProcessBlocks(project, kinds);

        Assert.True(applied.Changed);
        Assert.Equal(
            applied.AddedConnectionCount + applied.AddedStepCount + applied.RemovedStepCount,
            applied.ChangeCount);
        Assert.True(applied.AddedStepCount > 0);

        var repeated = _applier.ApplyProcessBlocks(project, kinds);

        Assert.Equal(RecipeConnectionProjectApplyOutcome.NoChanges, repeated.Outcome);
        Assert.Equal(0, repeated.ChangeCount);
    }
}
