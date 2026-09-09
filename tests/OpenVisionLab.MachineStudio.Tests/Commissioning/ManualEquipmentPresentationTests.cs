using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualEquipmentPresentationTests
{
    [Fact]
    public void SensorProjectionPreservesForceStateAndManualAvailability()
    {
        var presentation = new ManualEquipmentPresentation();
        presentation.ApplyProjection(CreateProjection(
            LayoutComponentKind.DigitalSensor,
            "sensor-1"));

        Assert.True(presentation.HasSelectedDigitalSensor);
        Assert.False(presentation.IsCurrentSensorFaulted);
        Assert.False(presentation.IsCurrentSensorManuallyForced);
        Assert.True(presentation.CanForceSensorOn);
        Assert.True(presentation.CanForceSensorOff);
        Assert.False(presentation.CanClearSensorForce);
        Assert.Equal("sensor-1", presentation.SelectedSensorId);
    }

    [Fact]
    public void CylinderAndConveyorProjectionPreservesFaultAndMotionGates()
    {
        var presentation = new ManualEquipmentPresentation();

        presentation.ApplyProjection(CreateProjection(
            LayoutComponentKind.PneumaticCylinder,
            "cylinder-1"));
        Assert.True(presentation.HasSelectedPneumaticCylinder);
        Assert.True(presentation.IsCurrentCylinderInterlocked);
        Assert.False(presentation.CanExtendCylinder);
        Assert.False(presentation.CanRetractCylinder);
        Assert.Equal("cylinder-1", presentation.SelectedCylinderId);

        presentation.ApplyProjection(CreateProjection(
            LayoutComponentKind.Conveyor,
            "conveyor-1"));
        Assert.True(presentation.HasSelectedConveyor);
        Assert.True(presentation.CanRunConveyorForward);
        Assert.True(presentation.CanRunConveyorReverse);
        Assert.False(presentation.CanStopConveyor);
        Assert.Equal(ConveyorDirection.Forward, presentation.SelectedConveyorDirection);
    }

    private static ManualEquipmentProjection CreateProjection(
        LayoutComponentKind selectedKind,
        string selectedId) => new(
        CreateSnapshot(),
        selectedId,
        selectedKind,
        IsRunMode: true,
        IsApplyingProject: false,
        IsValidationBusy: false,
        IsRuntimeDefinitionDirty: false,
        IsRunning: false,
        SimulationControlOwner.Manual,
        IsAutomaticRunActive: false,
        ActiveSequenceStatus: null);

    private static SimulationSnapshot CreateSnapshot() => new(
        TimeSpan.Zero,
        0,
        SimulationRunMode.Paused,
        SimulationControlOwner.Manual,
        1,
        [],
        0,
        [new DigitalSignalSnapshot("di.sensor-1", "Sensor", ChannelKind.DigitalInput, false)],
        [],
        [],
        AutomaticRunSnapshot.NotConfigured,
        [
            new LayoutComponentSnapshot(
                "sensor-1",
                "Sensor",
                LayoutComponentKind.DigitalSensor,
                0,
                0,
                0,
                10,
                10,
                false,
                null,
                SensorOutputChannelId: "di.sensor-1"),
            new LayoutComponentSnapshot(
                "cylinder-1",
                "Cylinder",
                LayoutComponentKind.PneumaticCylinder,
                0,
                0,
                0,
                10,
                10,
                false,
                null,
                CylinderState: PneumaticCylinderState.Extended),
            new LayoutComponentSnapshot(
                "conveyor-1",
                "Conveyor",
                LayoutComponentKind.Conveyor,
                0,
                0,
                0,
                10,
                10,
                false,
                null,
                ConveyorRunning: false,
                ConveyorDirection: ConveyorDirection.Forward)
        ],
        [new SimulationFaultSnapshot(
            SimulationFaultKind.CylinderTravelBlocked,
            "cylinder-1",
            null,
            0,
            TimeSpan.Zero)]);
}
