using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationFaultTargetCatalogTests
{
    [Fact]
    public void GetTargets_UsesOnlyDigitalInputsAndOrdersByRuntimeId()
    {
        var snapshot = CreateSnapshot(
            signals:
            [
                new DigitalSignalSnapshot("di.z", "Z sensor", ChannelKind.DigitalInput, false),
                new DigitalSignalSnapshot("do.command", "Command", ChannelKind.DigitalOutput, true),
                new DigitalSignalSnapshot("di.a", "A sensor", ChannelKind.DigitalInput, true)
            ],
            components: [],
            axes: []);

        var targets = new SimulationFaultTargetCatalog().GetTargets(
            snapshot,
            SimulationFaultKind.StuckDigitalInput);

        Assert.Collection(
            targets,
            target =>
            {
                Assert.Equal("di.a", target.Id);
                Assert.Equal("A sensor · di.a", target.DisplayName);
            },
            target => Assert.Equal("di.z", target.Id));
    }

    [Fact]
    public void GetTargets_UsesOnlyCylinderLayoutComponentsAndOrdersByRuntimeId()
    {
        var snapshot = CreateSnapshot(
            signals: [],
            components:
            [
                CreateComponent("cylinder.z", "Z clamp", LayoutComponentKind.PneumaticCylinder),
                CreateComponent("conveyor.a", "Conveyor", LayoutComponentKind.Conveyor),
                CreateComponent("cylinder.a", "A clamp", LayoutComponentKind.PneumaticCylinder)
            ],
            axes: []);

        var targets = new SimulationFaultTargetCatalog().GetTargets(
            snapshot,
            SimulationFaultKind.CylinderTravelBlocked);

        Assert.Collection(
            targets,
            target => Assert.Equal("cylinder.a", target.Id),
            target => Assert.Equal("cylinder.z", target.Id));
    }

    [Fact]
    public void GetTargets_UsesAxesAndOrdersByRuntimeId()
    {
        var snapshot = CreateSnapshot(
            signals: [],
            components: [],
            axes:
            [
                new AxisSnapshot("z", "Z Axis", AxisState.Idle, 0, 0),
                new AxisSnapshot("x", "X Axis", AxisState.Stopped, 10, 0)
            ]);

        var targets = new SimulationFaultTargetCatalog().GetTargets(
            snapshot,
            SimulationFaultKind.AxisMotionBlocked);
        var followingErrorTargets = new SimulationFaultTargetCatalog().GetTargets(
            snapshot,
            SimulationFaultKind.AxisFollowingError);

        Assert.Collection(
            targets,
            target => Assert.Equal("x", target.Id),
            target => Assert.Equal("z", target.Id));
        Assert.Equal(
            targets.Select(target => target.Id),
            followingErrorTargets.Select(target => target.Id));
    }

    private static SimulationSnapshot CreateSnapshot(
        IEnumerable<DigitalSignalSnapshot> signals,
        IEnumerable<LayoutComponentSnapshot> components,
        IEnumerable<AxisSnapshot> axes) =>
        new(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            axes,
            0,
            signals,
            [],
            [],
            AutomaticRunSnapshot.NotConfigured,
            components);

    private static LayoutComponentSnapshot CreateComponent(
        string id,
        string name,
        LayoutComponentKind kind) =>
        new(
            id,
            name,
            kind,
            X: 0,
            Y: 0,
            RotationDegrees: 0,
            Width: 10,
            Height: 10,
            IsDetected: null,
            PendingTransitionTicks: null);
}
