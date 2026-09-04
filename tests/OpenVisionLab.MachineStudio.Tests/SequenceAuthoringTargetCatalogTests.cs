using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SequenceAuthoringTargetCatalogTests
{
    [Fact]
    public void Build_PreservesAuthoringOrderAndAxisDefaultParameter()
    {
        MachineProjectDocument project = CreateProject();
        var catalog = new SequenceAuthoringTargetCatalog();

        SequenceAuthoringTargetCatalogSnapshot result = catalog.Build(project);

        Assert.Equal(
            new[] { "di.ready", "do.run", "axis-1", "camera-1", "parent", "child" },
            result.AuthoringTargets.Select(target => target.Id));
        Assert.Equal("Ready · di.ready", result.AuthoringTargets[0].Name);
        Assert.Equal("12.5", result.AuthoringTargets[2].DefaultParameter);
        Assert.Equal("camera-1", result.AuthoringTargets[3].Id);
        Assert.Equal(SequenceAuthoringTargetKind.Subsequence, result.AuthoringTargets[4].Kind);
    }

    [Fact]
    public void Build_UsesActiveLayoutForExpectedStateTargets()
    {
        MachineProjectDocument project = CreateProject();
        var catalog = new SequenceAuthoringTargetCatalog();

        SequenceAuthoringTargetCatalogSnapshot result = catalog.Build(project);

        Assert.Equal(
            new[] { "axis-1", "cylinder-1", "conveyor-1", "sensor-1", "workpiece-1" },
            result.ExpectedStateTargets.Select(target => target.Id));
        Assert.Equal(new[] { "Stopped", "ForwardRunning", "ReverseRunning" },
            result.ExpectedStateTargets.Single(target => target.Id == "conveyor-1").States);
        Assert.DoesNotContain(result.ExpectedStateTargets, target => target.Id == "inactive-sensor");
    }

    [Fact]
    public void GetTargetsForSequence_ExcludesOnlyTheSelectedSubsequence()
    {
        MachineProjectDocument project = CreateProject();
        var catalog = new SequenceAuthoringTargetCatalog();
        SequenceAuthoringTargetCatalogSnapshot result = catalog.Build(project);

        IReadOnlyList<SequenceAuthoringTarget> targets = catalog.GetTargetsForSequence(
            result.AuthoringTargets,
            project.Sequences[0]);

        Assert.DoesNotContain(targets, target => target.Id == "parent");
        Assert.Contains(targets, target => target.Id == "child");
        Assert.Contains(targets, target => target.Id == "axis-1");
    }

    [Fact]
    public void BuildCompilationTargets_AllowsProjectBackedSequenceReferences()
    {
        MachineProjectDocument project = CreateProject();
        var catalog = new SequenceAuthoringTargetCatalog();
        SequenceCompilationTargets targets = catalog.BuildCompilationTargets(project);
        SequenceDefinition sequence = new()
        {
            Id = "parent",
            Steps =
            [
                new SequenceStepDefinition
                {
                    Id = "set",
                    Action = SequenceStepAction.SetSignal,
                    TargetId = "do.run",
                    Parameter = "true",
                    NextStepId = "move"
                },
                new SequenceStepDefinition
                {
                    Id = "move",
                    Action = SequenceStepAction.MoveAxis,
                    TargetId = "axis-1",
                    Parameter = "12.5",
                    NextStepId = "camera"
                },
                new SequenceStepDefinition
                {
                    Id = "camera",
                    Action = SequenceStepAction.TriggerCamera,
                    TargetId = "camera-1",
                    Parameter = "default",
                    NextStepId = "call"
                },
                new SequenceStepDefinition
                {
                    Id = "call",
                    Action = SequenceStepAction.CallSubsequence,
                    TargetId = "child",
                    NextStepId = "complete"
                },
                new SequenceStepDefinition
                {
                    Id = "complete",
                    Action = SequenceStepAction.Complete
                }
            ]
        };

        SequenceCompilationResult compilation = new SequenceCompiler().Compile(sequence, targets);

        Assert.True(compilation.IsSuccess, string.Join(" | ", compilation.Errors.Select(error => error.Message)));
    }

    private static MachineProjectDocument CreateProject() =>
        new()
        {
            Channels =
            [
                new ChannelDefinition { Id = "di.ready", Name = "Ready", Kind = ChannelKind.DigitalInput },
                new ChannelDefinition { Id = "do.run", Name = "Run", Kind = ChannelKind.DigitalOutput },
                new ChannelDefinition { Id = "ai.pressure", Name = "Pressure", Kind = ChannelKind.AnalogInput }
            ],
            Axes =
            [
                new OpenVisionLab.Machine.Core.Axes.VirtualAxisDefinition
                {
                    Id = "axis-1",
                    Name = "Transfer Axis",
                    HomePosition = 12.5
                }
            ],
            Devices =
            [
                new DeviceDefinition { Id = "camera-1", Name = "Top Camera", Kind = DeviceKind.Camera },
                new DeviceDefinition { Id = "light-1", Name = "Light", Kind = DeviceKind.Light }
            ],
            Sequences =
            [
                new SequenceDefinition { Id = "parent", Name = "Parent" },
                new SequenceDefinition { Id = "child", Name = "Child" }
            ],
            Simulation = new SimulationDefinition { ActiveLayoutId = "active" },
            Layouts =
            [
                new MachineLayoutDefinition
                {
                    Id = "inactive",
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "inactive-sensor",
                            Kind = LayoutComponentKind.DigitalSensor
                        }
                    ]
                },
                new MachineLayoutDefinition
                {
                    Id = "active",
                    Components =
                    [
                        new LayoutComponentDefinition
                        {
                            Id = "cylinder-1",
                            Name = "Clamp",
                            Kind = LayoutComponentKind.PneumaticCylinder
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "conveyor-1",
                            Name = "Transfer Conveyor",
                            Kind = LayoutComponentKind.Conveyor
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "sensor-1",
                            Name = "Presence Sensor",
                            Kind = LayoutComponentKind.DigitalSensor
                        },
                        new LayoutComponentDefinition
                        {
                            Id = "workpiece-1",
                            Name = "Wafer",
                            Kind = LayoutComponentKind.Workpiece
                        }
                    ]
                }
            ]
        };
}
