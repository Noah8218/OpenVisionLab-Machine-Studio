using System.Text.Json;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SequenceTaskOverviewTests
{
    [Fact]
    public void GroupsOnlyAuthoredRelationshipsAndLeavesDefinitionsInOrder()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        string before = JsonSerializer.Serialize(project);
        var groups = SequenceTaskOverview.Build(project, project.Sequences[0]);
        Assert.Equal(new[] { 1, 3, 3, 1, 1, 1, 1 }, groups.Select(group => group.StepIndices.Count));
        Assert.Equal(SequenceTaskKind.PositionAndCapture, groups[1].Kind);
        Assert.Equal(SequenceTaskKind.PositionAndCapture, groups[2].Kind);
        Assert.Equal(Enumerable.Range(0, 11), groups.SelectMany(group => group.StepIndices));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("wait-error")]
    [InlineData("external-entry")]
    [InlineData("missing-axis")]
    [InlineData("wrong-axis")]
    [InlineData("duplicate-id")]
    public void KeepsAmbiguousOrBranchedStepsSeparate(string boundary)
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        var sequence = project.Sequences[0];
        switch (boundary)
        {
            case "error": sequence.Steps[1].ErrorStepId = "done"; break;
            case "wait-error": sequence.Steps[2].ErrorStepId = "done"; break;
            case "external-entry": sequence.Steps[8].NextStepId = "wait-1"; break;
            case "missing-axis": project.Axes.Clear(); break;
            case "wrong-axis": sequence.Steps[2].TargetId = "other-axis"; break;
            case "duplicate-id": sequence.Steps[8].Id = "wait-1"; break;
        }
        var group = SequenceTaskOverview.Build(project, sequence).Single(item => item.StepIndices.Contains(1));
        Assert.Single(group.StepIndices);
    }

    [Fact]
    public void CameraOnAnotherAxisDoesNotJoinThePositionGroup()
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        var camera = project.Devices[0].Camera!;
        camera.Geometry = camera.Geometry! with { AxisId = "another-axis" };
        var groups = SequenceTaskOverview.Build(project, project.Sequences[0]);
        Assert.Equal(SequenceTaskKind.Position, groups[1].Kind);
        Assert.Equal(new[] { 1, 2 }, groups[1].StepIndices);
        Assert.Equal(new[] { 3 }, groups[2].StepIndices);
    }

    [Theory]
    [InlineData(true, "extended", 2)]
    [InlineData(false, "retracted", 2)]
    [InlineData(true, "retracted", 1)]
    public void CylinderRequiresItsAuthoredCommandAndMatchingFeedback(bool extend, string feedback, int count)
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Devices.Add(new DeviceDefinition { Id = "cylinder", Kind = DeviceKind.Cylinder, Cylinder = new PneumaticCylinderDefinition { ExtendCommandChannelId = "extend", ExtendedSensorChannelId = "extended", RetractedSensorChannelId = "retracted" } });
        var sequence = new SequenceDefinition { Steps = [new() { Id = "command", Action = SequenceStepAction.SetSignal, TargetId = "extend", Parameter = extend.ToString() }, new() { Id = "feedback", Action = SequenceStepAction.WaitSignal, TargetId = feedback, Parameter = "true" }, new() { Id = "end", Action = SequenceStepAction.Complete }] };
        Assert.Equal(count, SequenceTaskOverview.Build(project, sequence)[0].StepIndices.Count);
    }

    [Theory]
    [InlineData("unit", 3)]
    [InlineData("other-unit", 1)]
    public void ConveyorRequiresTheSameUnitSensorAndExplicitStop(string sensorUnit, int count)
    {
        var project = R40ProcessWorkspaceTests.CreateProject();
        project.Devices.Add(new DeviceDefinition { Id = "belt", Kind = DeviceKind.Conveyor, Conveyor = new ConveyorDefinition { RunCommandChannelId = "run" } });
        project.Devices.Add(new DeviceDefinition { Id = "sensor", Kind = DeviceKind.Sensor, Sensor = new DigitalSensorDefinition { OutputChannelId = "detected" } });
        project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "belt-view", BehaviorBindingId = "belt", UnitId = "unit" });
        project.Layouts[0].Components.Add(new LayoutComponentDefinition { Id = "sensor-view", BehaviorBindingId = "sensor", UnitId = sensorUnit });
        var sequence = new SequenceDefinition { UnitId = "unit", Steps = [new() { Id = "start", Action = SequenceStepAction.SetSignal, TargetId = "run", Parameter = "true" }, new() { Id = "wait", Action = SequenceStepAction.WaitSignal, TargetId = "detected", Parameter = "true" }, new() { Id = "stop", Action = SequenceStepAction.SetSignal, TargetId = "run", Parameter = "false" }, new() { Id = "end", Action = SequenceStepAction.Complete }] };
        Assert.Equal(count, SequenceTaskOverview.Build(project, sequence)[0].StepIndices.Count);
    }
}
