using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineLayoutSignalBindingValidatorTests
{
    [Fact]
    public void ValidateAcceptsConfiguredDigitalSensorBinding()
    {
        var sensor = new DigitalSensorRuntimeState(new DigitalSensorRuntimeConfiguration(
            "sensor-1",
            "Sensor",
            "di.sensor-1",
            "frame-1",
            0,
            0,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(2, 2)));
        var validator = new MachineLayoutSignalBindingValidator(
            CreateHub(Channel("di.sensor-1", ChannelKind.DigitalInput)));

        validator.Validate(
            new[] { sensor },
            Array.Empty<PneumaticCylinderRuntimeState>(),
            Array.Empty<ConveyorRuntimeState>(),
            Array.Empty<LoadLockRuntimeState>(),
            Array.Empty<WaferHandlerRuntimeState>(),
            Array.Empty<InspectionSortRouterRuntimeState>(),
            Array.Empty<InspectionHandoffRuntimeState>(),
            Array.Empty<OhtHandoffRuntimeState>(),
            Array.Empty<PrealignerRuntimeState>());
    }

    [Fact]
    public void ValidateRejectsMissingDigitalSensorBinding()
    {
        var sensor = new DigitalSensorRuntimeState(new DigitalSensorRuntimeConfiguration(
            "sensor-1",
            "Sensor",
            "di.missing",
            "frame-1",
            0,
            0,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(2, 2)));
        var validator = new MachineLayoutSignalBindingValidator(CreateHub());

        var exception = Assert.Throws<ArgumentException>(() => validator.Validate(
            new[] { sensor },
            Array.Empty<PneumaticCylinderRuntimeState>(),
            Array.Empty<ConveyorRuntimeState>(),
            Array.Empty<LoadLockRuntimeState>(),
            Array.Empty<WaferHandlerRuntimeState>(),
            Array.Empty<InspectionSortRouterRuntimeState>(),
            Array.Empty<InspectionHandoffRuntimeState>(),
            Array.Empty<OhtHandoffRuntimeState>(),
            Array.Empty<PrealignerRuntimeState>()));

        Assert.Equal("_signalHub", exception.ParamName);
        Assert.Contains("di.missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsCylinderCommandWithWrongChannelKind()
    {
        var cylinder = new PneumaticCylinderRuntimeState(new PneumaticCylinderRuntimeConfiguration(
            "cylinder-1",
            "Cylinder",
            "do.extend",
            "di.extended",
            "di.retracted",
            2,
            2,
            0,
            0,
            10,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(2, 2)));
        var validator = new MachineLayoutSignalBindingValidator(CreateHub(
            Channel("do.extend", ChannelKind.DigitalInput),
            Channel("di.extended", ChannelKind.DigitalInput),
            Channel("di.retracted", ChannelKind.DigitalInput)));

        var exception = Assert.Throws<ArgumentException>(() => validator.Validate(
            Array.Empty<DigitalSensorRuntimeState>(),
            new[] { cylinder },
            Array.Empty<ConveyorRuntimeState>(),
            Array.Empty<LoadLockRuntimeState>(),
            Array.Empty<WaferHandlerRuntimeState>(),
            Array.Empty<InspectionSortRouterRuntimeState>(),
            Array.Empty<InspectionHandoffRuntimeState>(),
            Array.Empty<OhtHandoffRuntimeState>(),
            Array.Empty<PrealignerRuntimeState>()));

        Assert.Equal("_signalHub", exception.ParamName);
        Assert.Contains("DigitalOutput", exception.Message, StringComparison.Ordinal);
    }

    private static DeterministicSignalHub CreateHub(params ChannelDefinition[] channels) =>
        DeterministicSignalHub.Create(channels).Hub!;

    private static ChannelDefinition Channel(string id, ChannelKind kind) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = 0
        };
}
