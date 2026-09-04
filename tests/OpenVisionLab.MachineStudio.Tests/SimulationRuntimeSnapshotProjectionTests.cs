using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationRuntimeSnapshotProjectionTests
{
    [Fact]
    public void ProjectsSelectedRuntimeItemsAndTargetIds()
    {
        var snapshot = CreateSnapshot(
            SimulationRunMode.RealTime,
            SimulationControlOwner.Manual,
            axes:
            [
                new AxisSnapshot("axis-y", "Y Axis", AxisState.Idle, 2, 0),
                new AxisSnapshot("axis-x", "X Axis", AxisState.Idle, 1, 0)
            ],
            signals:
            [
                new DigitalSignalSnapshot("di.cycle-start", "Cycle start", ChannelKind.DigitalInput, true),
                new DigitalSignalSnapshot("do.cycle-active", "Cycle active", ChannelKind.DigitalOutput, false),
                new DigitalSignalSnapshot("do.cycle-done", "Cycle done", ChannelKind.DigitalOutput, true)
            ],
            sequences:
            [
                CreateSequence("main"),
                CreateSequence("recovery")
            ],
            cameras:
            [
                new VirtualCameraSnapshot("camera-1", "Camera 1", VirtualCameraState.Idle, 1, null, null, 0, 0, null),
                new VirtualCameraSnapshot("camera-2", "Camera 2", VirtualCameraState.FrameReady, 2, "acq-2", "recipe", 0, 0, null)
            ],
            layoutComponents:
            [
                new LayoutComponentSnapshot("cylinder-1", "Cylinder", LayoutComponentKind.PneumaticCylinder, 0, 0, 0, 1, 1, null, null),
                new LayoutComponentSnapshot("frame-1", "Frame", LayoutComponentKind.MachineFrame, 0, 0, 0, 1, 1, null, null)
            ]);

        var projection = SimulationRuntimeSnapshotProjection.Create(
            snapshot,
            new(
                LayoutComponentKind.LinearStage,
                "axis-x",
                "axis-y",
                "camera-2",
                SimulationFaultKind.AxisMotionBlocked,
                "main"));

        Assert.Equal(TimeSpan.FromMilliseconds(25), projection.SimulationTime);
        Assert.Equal(7, projection.TickIndex);
        Assert.Equal(SimulationRunMode.RealTime, projection.RunMode);
        Assert.Equal(SimulationControlOwner.Manual, projection.ControlOwner);
        Assert.True(projection.IsRunning);
        Assert.Equal("axis-x", projection.CurrentAxis?.Id);
        Assert.Equal("camera-2", projection.CurrentCamera?.Id);
        Assert.Equal("main", projection.CurrentSequence?.SequenceId);
        Assert.Equal(true, projection.CycleStartInput);
        Assert.Equal(false, projection.CycleActiveOutput);
        Assert.Equal(true, projection.CycleDoneOutput);
        Assert.Equal(
            new[] { "axis-y", "axis-x", "cylinder-1", "frame-1" },
            projection.ScenarioTargetIds);
        Assert.Equal(projection.ScenarioTargetIds, projection.FinalEquipmentTargetIds);
        Assert.Equal(new[] { "axis-x", "axis-y" }, projection.ScheduledFaultTargetIds);
        Assert.Equal(new[] { "main", "recovery" }, projection.RecoverySequenceIds);
    }

    [Fact]
    public void UsesTreeAxisAndFirstCameraFallbacks()
    {
        var snapshot = CreateSnapshot(
            SimulationRunMode.Paused,
            SimulationControlOwner.EmbeddedSequence,
            axes:
            [
                new AxisSnapshot("axis-y", "Y Axis", AxisState.Idle, 2, 0),
                new AxisSnapshot("axis-x", "X Axis", AxisState.Idle, 1, 0)
            ],
            cameras:
            [
                new VirtualCameraSnapshot("camera-1", "Camera 1", VirtualCameraState.Idle, 1, null, null, 0, 0, null)
            ]);

        var projection = SimulationRuntimeSnapshotProjection.Create(
            snapshot,
            new(
                LayoutComponentKind.DigitalSensor,
                "ignored-binding",
                "axis-y",
                "missing-camera",
                SimulationFaultKind.AxisFollowingError,
                "missing-sequence"));

        Assert.False(projection.IsRunning);
        Assert.Equal("axis-y", projection.CurrentAxis?.Id);
        Assert.Equal("camera-1", projection.CurrentCamera?.Id);
        Assert.Null(projection.CurrentSequence);
        Assert.Null(projection.CycleStartInput);
        Assert.Null(projection.CycleActiveOutput);
        Assert.Null(projection.CycleDoneOutput);
    }

    [Fact]
    public void UsesFirstAxisWhenNoTreeAxisIsSelected()
    {
        var snapshot = CreateSnapshot(
            SimulationRunMode.SingleStep,
            SimulationControlOwner.Definition,
            axes:
            [
                new AxisSnapshot("axis-first", "First", AxisState.Idle, 0, 0),
                new AxisSnapshot("axis-second", "Second", AxisState.Idle, 0, 0)
            ]);

        var projection = SimulationRuntimeSnapshotProjection.Create(
            snapshot,
            new(
                null,
                null,
                null,
                null,
                SimulationFaultKind.AxisMotionBlocked,
                null));

        Assert.False(projection.IsRunning);
        Assert.Equal("axis-first", projection.CurrentAxis?.Id);
    }

    private static SimulationSnapshot CreateSnapshot(
        SimulationRunMode runMode,
        SimulationControlOwner controlOwner,
        IEnumerable<AxisSnapshot>? axes = null,
        IEnumerable<DigitalSignalSnapshot>? signals = null,
        IEnumerable<SequenceExecutionSnapshot>? sequences = null,
        IEnumerable<VirtualCameraSnapshot>? cameras = null,
        IEnumerable<LayoutComponentSnapshot>? layoutComponents = null) => new(
        TimeSpan.FromMilliseconds(25),
        7,
        runMode,
        controlOwner,
        1,
        axes ?? [],
        1,
        signals ?? [],
        sequences ?? [],
        cameras ?? [],
        AutomaticRunSnapshot.NotConfigured,
        layoutComponents ?? []);

    private static SequenceExecutionSnapshot CreateSequence(string id) => new(
        id,
        SequenceExecutionStatus.Ready,
        null,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        null,
        TimeSpan.FromSeconds(10));
}
