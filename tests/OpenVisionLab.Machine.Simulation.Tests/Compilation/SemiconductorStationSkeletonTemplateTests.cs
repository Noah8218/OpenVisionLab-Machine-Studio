using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Sequences;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SemiconductorStationSkeletonTemplateTests
{
    private readonly ProjectDocumentStore _store = new();
    private readonly SemiconductorStationSkeletonTemplate _template = new();

    [Theory]
    [MemberData(
        nameof(DeterministicRecipeDryRunRunnerTests.SemiconductorRecipeFiles),
        MemberType = typeof(DeterministicRecipeDryRunRunnerTests))]
    public void ExistingSemiconductorRecipe_RecognizesAndKeepsAllRoles(string fileName)
    {
        var project = _store.Load(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            fileName)));
        var before = _store.Serialize(project);

        var preview = _template.Preview(project);

        Assert.Equal(0, preview.ProposedCount);
        Assert.Equal(10, preview.ExistingCount);
        Assert.Equal(0, preview.UnavailableCount);
        Assert.Equal(before, _store.Serialize(project));
    }

    [Fact]
    public async Task BlankProject_PreviewsWithoutMutation_ThenBuildsRunnableStation()
    {
        var project = BlankProject();
        var before = _store.Serialize(project);

        var preview = _template.Preview(project);

        Assert.Equal(before, _store.Serialize(project));
        Assert.True(preview.CanApply);
        Assert.Equal(10, preview.ProposedCount);
        Assert.Equal(0, preview.UnavailableCount);

        var result = _template.Apply(project);

        Assert.Equal(10, result.AppliedCount);
        var layout = Assert.Single(project.Layouts);
        Assert.Equal(7, layout.Components.Count);
        Assert.Single(project.Axes);
        Assert.Equal(5, project.Devices.Count);
        Assert.Equal(9, project.Channels.Count);
        var sequence = Assert.Single(project.Sequences);
        Assert.Equal(12, sequence.Steps.Count);
        Assert.Equal(sequence.Id, project.Simulation.AutomaticRun?.SequenceId);
        Assert.All(layout.Components, component =>
            Assert.False(string.IsNullOrWhiteSpace(component.Id)));

        var compilation = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequence.Id);
        Assert.Equal(RecipeDryRunOutcome.Completed, dryRun.Outcome);
        Assert.Equal(12, dryRun.Timeline.Count);
    }

    [Fact]
    public async Task AppliedStation_RoundTripsAndSecondPreviewKeepsEveryRole()
    {
        var project = BlankProject();
        _template.Apply(project);
        var reloaded = _store.Load(_store.Serialize(project));

        var preview = _template.Preview(reloaded);

        Assert.False(preview.CanApply);
        Assert.Equal(0, preview.ProposedCount);
        Assert.Equal(10, preview.ExistingCount);
        Assert.Equal(0, preview.UnavailableCount);
        var sequence = Assert.Single(reloaded.Sequences);
        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(reloaded, sequence.Id);
        Assert.Equal(RecipeDryRunOutcome.Completed, dryRun.Outcome);
    }

    [Fact]
    public async Task CustomSetup_AppliesToEquipmentAndRoundTripsWithoutRunning()
    {
        var project = BlankProject();
        var setup = new SemiconductorStationSetupDefinition
        {
            StationName = "Lithography Transfer A",
            WaferType = "200 mm Wafer",
            AxisTravel = 460,
            TransportSpeed = 175,
            EntrySensorPosition = 145,
            ProcessSensorPosition = 510,
            CylinderTravelTimeMilliseconds = 180
        };
        var before = _store.Serialize(project);

        var preview = _template.Preview(project, setup);

        Assert.Equal(before, _store.Serialize(project));
        Assert.True(preview.CanApply);
        var result = _template.Apply(project, setup);
        Assert.True(result.Changed);
        Assert.Equal(10, result.AppliedCount);
        Assert.Equal(MachineProjectDocument.CurrentSchema, project.Schema);
        Assert.Equal(setup, project.SemiconductorStationSetup);
        Assert.Equal(setup.StationName, Assert.Single(project.Layouts).Name);
        Assert.Equal(setup.AxisTravel, Assert.Single(project.Axes).SoftLimitMax);
        Assert.Equal(
            setup.TransportSpeed,
            Assert.Single(project.Devices, device => device.Kind == DeviceKind.Conveyor).Conveyor?.SpeedUnitsPerSecond);
        Assert.Equal(
            setup.WaferType,
            Assert.Single(project.Devices, device => device.Kind == DeviceKind.Workpiece).Workpiece?.Type);
        var sensors = project.Layouts[0].Components
            .Where(component => component.Kind == LayoutComponentKind.DigitalSensor)
            .OrderBy(component => component.Transform.X)
            .ToArray();
        Assert.Equal(setup.EntrySensorPosition, sensors[0].Transform.X);
        Assert.Equal(setup.ProcessSensorPosition, sensors[1].Transform.X);
        var cylinder = Assert.Single(project.Devices, device => device.Kind == DeviceKind.Cylinder).Cylinder;
        Assert.Equal(setup.CylinderTravelTimeMilliseconds, cylinder?.ExtendDurationMilliseconds);
        Assert.Equal(setup.CylinderTravelTimeMilliseconds, cylinder?.RetractDurationMilliseconds);

        var reloaded = _store.Load(_store.Serialize(project));
        Assert.Equal(setup, reloaded.SemiconductorStationSetup);
        Assert.Equal(setup, _template.ResolveSetup(reloaded));
        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(
            reloaded,
            Assert.Single(reloaded.Sequences).Id);
        Assert.Equal(RecipeDryRunOutcome.Completed, dryRun.Outcome);
    }

    [Fact]
    public void ConfirmedSetup_CanBeEditedAndAppliedToTheManagedStation()
    {
        var project = BlankProject();
        _template.Apply(project);
        var edited = project.SemiconductorStationSetup! with
        {
            AxisTravel = 500,
            TransportSpeed = 190,
            EntrySensorPosition = 160,
            ProcessSensorPosition = 520,
            CylinderTravelTimeMilliseconds = 210
        };

        var result = _template.Apply(project, edited);

        Assert.True(result.Changed);
        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(edited, project.SemiconductorStationSetup);
        Assert.Equal(500, Assert.Single(project.Axes).SoftLimitMax);
        Assert.Equal(
            190,
            Assert.Single(project.Devices, device => device.Kind == DeviceKind.Conveyor).Conveyor?.SpeedUnitsPerSecond);
        Assert.Equal(
            210,
            Assert.Single(project.Devices, device => device.Kind == DeviceKind.Cylinder).Cylinder?.ExtendDurationMilliseconds);
    }

    [Fact]
    public void InvalidSetup_IsRejectedWithoutChangingProject()
    {
        var project = BlankProject();
        var before = _store.Serialize(project);
        var invalid = new SemiconductorStationSetupDefinition
        {
            EntrySensorPosition = 500,
            ProcessSensorPosition = 100
        };

        Assert.False(SemiconductorStationSkeletonTemplate.IsValidSetup(invalid));
        Assert.Throws<ArgumentException>(() => _template.Apply(project, invalid));
        Assert.Equal(before, _store.Serialize(project));
    }

    [Fact]
    public void InvalidPersistedSetup_IsNotRestored()
    {
        var project = BlankProject();
        project.SemiconductorStationSetup = new SemiconductorStationSetupDefinition
        {
            AxisTravel = -1
        };

        var restored = _template.ResolveSetup(project);

        Assert.True(SemiconductorStationSkeletonTemplate.IsValidSetup(restored));
        Assert.Equal(SemiconductorStationSetupDefinition.DefaultAxisTravel, restored.AxisTravel);
    }

    [Fact]
    public void PartialStation_AddsOnlyMissingRolesAndPreservesUserDefinitions()
    {
        var project = BlankProject();
        _template.Apply(project);
        var layout = Assert.Single(project.Layouts);
        layout.Components.Remove(Assert.Single(layout.Components, component =>
            component.Kind == LayoutComponentKind.MachineFrame));
        layout.Components.Remove(Assert.Single(layout.Components, component =>
            component.Id == "sensor-process"));
        project.Devices.Remove(Assert.Single(project.Devices, device =>
            device.Id == "device.sensor-process"));
        project.Channels.Remove(Assert.Single(project.Channels, channel =>
            channel.Id == "di.sensor-process"));
        project.Channels.Add(new ChannelDefinition
        {
            Id = "do.user-owned",
            Name = "User-owned output",
            Kind = ChannelKind.DigitalOutput
        });
        Assert.Single(project.Sequences).Name = "User-authored automatic cycle";
        var projectId = project.Id;

        var preview = _template.Preview(project);
        var result = _template.Apply(project);

        Assert.True(preview.CanApply);
        Assert.Equal(3, preview.ProposedCount);
        Assert.Equal(3, result.AppliedCount);
        Assert.Equal(projectId, project.Id);
        Assert.Contains(project.Channels, channel => channel.Id == "do.user-owned");
        Assert.Equal("User-authored automatic cycle", Assert.Single(project.Sequences).Name);
        Assert.Single(project.Layouts[0].Components, component => component.Kind == LayoutComponentKind.MachineFrame);
        Assert.Single(project.Layouts[0].Components, component => component.Id == "sensor-process");
        Assert.Single(project.Devices, device => device.Id == "device.sensor-process");
        Assert.Single(project.Channels, channel => channel.Id == "di.sensor-process");
    }

    [Fact]
    public void InvalidExistingRole_BlocksApplyWithoutChangingProject()
    {
        var project = BlankProject();
        project.Layouts.Add(new MachineLayoutDefinition
        {
            Id = "existing-layout",
            Name = "Existing layout",
            Components =
            {
                new LayoutComponentDefinition
                {
                    Id = "broken-transport",
                    Name = "Broken transport",
                    Kind = LayoutComponentKind.Conveyor,
                    BehaviorBindingId = "missing-device"
                }
            }
        });
        project.Simulation.ActiveLayoutId = "existing-layout";
        var before = _store.Serialize(project);

        var preview = _template.Preview(project);
        var result = _template.Apply(project);

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Entries, entry =>
            entry.Role == SemiconductorStationSkeletonRole.Transport
            && entry.Status == SemiconductorStationSkeletonStatus.Unavailable
            && entry.UnavailableReason == SemiconductorStationSkeletonUnavailableReason.ExistingRoleInvalid);
        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(before, _store.Serialize(project));
    }

    private static MachineProjectDocument BlankProject() => new()
    {
        Id = "station-skeleton-test",
        Name = "Station skeleton test"
    };

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult result) => string.Join(
        Environment.NewLine,
        result.Errors.Select(error => $"{error.Code} [{error.TargetId}]: {error.Message}"));
}
