using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineProjectRuntimeComponentCompilerTests
{
    [Fact]
    public void Compile_ActiveComponents_ReturnsTypedRuntimeRecords()
    {
        var compiler = new MachineProjectRuntimeComponentCompiler(
            new FixedStepDelayConverter(TimeSpan.FromMilliseconds(5)));
        var errors = new List<MachineProjectRuntimeCompilationError>();

        IReadOnlyList<LayoutComponentRuntimeConfiguration> result = compiler.Compile(
            CreateLayout(),
            Axes(),
            Devices(),
            errors);

        Assert.Empty(errors);
        Assert.Collection(
            result,
            frame => Assert.IsType<MachineFrameRuntimeConfiguration>(frame),
            stage => Assert.Equal("axis.x", Assert.IsType<LinearStageRuntimeConfiguration>(stage).AxisId),
            conveyor => Assert.Equal(0.6, Assert.IsType<ConveyorRuntimeConfiguration>(conveyor).TravelPerTick, precision: 10),
            workpiece => Assert.Equal("conveyor", Assert.IsType<WorkpieceRuntimeConfiguration>(workpiece).ConveyorComponentId),
            sensor => Assert.Equal(2, Assert.IsType<DigitalSensorRuntimeConfiguration>(sensor).OnDelayTicks),
            cylinder => Assert.Equal(60, Assert.IsType<PneumaticCylinderRuntimeConfiguration>(cylinder).Stroke));
    }

    [Fact]
    public void Compile_SensorTargetOutsideActiveLayout_ReturnsTypedError()
    {
        MachineLayoutDefinition layout = CreateLayout();
        var devices = Devices();
        devices["device.sensor"].Sensor!.TargetComponentId = "missing";
        var errors = new List<MachineProjectRuntimeCompilationError>();

        IReadOnlyList<LayoutComponentRuntimeConfiguration> result = new MachineProjectRuntimeComponentCompiler(
            new FixedStepDelayConverter(TimeSpan.FromMilliseconds(5))).Compile(
                layout,
                Axes(),
                devices,
                errors);

        Assert.DoesNotContain(result, component => component.Id == "sensor");
        var error = Assert.Single(errors);
        Assert.Equal(MachineProjectRuntimeCompilationErrorCode.LayoutTargetOutsideActiveLayout, error.Code);
        Assert.Equal("sensor", error.TargetId);
    }

    [Fact]
    public void Compile_InvalidCylinderTiming_ReturnsTypedError()
    {
        MachineLayoutDefinition layout = CreateLayout();
        var devices = Devices();
        devices["device.cylinder"].Cylinder!.ExtendDurationMilliseconds = 0;
        var errors = new List<MachineProjectRuntimeCompilationError>();

        IReadOnlyList<LayoutComponentRuntimeConfiguration> result = new MachineProjectRuntimeComponentCompiler(
            new FixedStepDelayConverter(TimeSpan.FromMilliseconds(5))).Compile(
                layout,
                Axes(),
                devices,
                errors);

        Assert.DoesNotContain(result, component => component.Id == "cylinder");
        var error = Assert.Single(errors);
        Assert.Equal(MachineProjectRuntimeCompilationErrorCode.CylinderTimingInvalid, error.Code);
        Assert.Equal("cylinder", error.TargetId);
    }

    private static MachineLayoutDefinition CreateLayout() => new()
    {
        Id = "layout",
        Name = "Layout",
        Components =
        [
            Component("frame", "Frame", LayoutComponentKind.MachineFrame),
            Component("stage", "Stage", LayoutComponentKind.LinearStage, "axis.x"),
            Component("conveyor", "Conveyor", LayoutComponentKind.Conveyor, "device.conveyor"),
            Component("workpiece", "Workpiece", LayoutComponentKind.Workpiece, "device.workpiece"),
            Component("sensor", "Sensor", LayoutComponentKind.DigitalSensor, "device.sensor"),
            Component("cylinder", "Cylinder", LayoutComponentKind.PneumaticCylinder, "device.cylinder")
        ]
    };

    private static LayoutComponentDefinition Component(
        string id,
        string name,
        LayoutComponentKind kind,
        string? bindingId = null) => new()
        {
            Id = id,
            Name = name,
            Kind = kind,
            BehaviorBindingId = bindingId,
            Transform = new Transform2D { X = 10, Y = 20 },
            Size = new Size2D { Width = 20, Height = 20 }
        };

    private static IReadOnlyDictionary<string, VirtualAxisDefinition> Axes() =>
        new Dictionary<string, VirtualAxisDefinition>(StringComparer.Ordinal)
        {
            ["axis.x"] = new()
            {
                Id = "axis.x",
                Name = "X",
                Kind = AxisKind.Linear,
                HomePosition = 0,
                SoftLimitMin = 0,
                SoftLimitMax = 100
            }
        };

    private static Dictionary<string, DeviceDefinition> Devices() =>
        new(StringComparer.Ordinal)
        {
            ["device.conveyor"] = new DeviceDefinition
            {
                Id = "device.conveyor",
                Name = "Conveyor",
                Kind = DeviceKind.Conveyor,
                Conveyor = new ConveyorDefinition
                {
                    RunCommandChannelId = "do.run",
                    ReverseCommandChannelId = "do.reverse",
                    SpeedUnitsPerSecond = 120
                }
            },
            ["device.workpiece"] = new DeviceDefinition
            {
                Id = "device.workpiece",
                Name = "Workpiece",
                Kind = DeviceKind.Workpiece,
                Workpiece = new WorkpieceDefinition
                {
                    Type = "Part",
                    ConveyorComponentId = "conveyor",
                    InspectionState = WorkpieceInspectionState.Pending
                }
            },
            ["device.sensor"] = new DeviceDefinition
            {
                Id = "device.sensor",
                Name = "Sensor",
                Kind = DeviceKind.Sensor,
                Sensor = new DigitalSensorDefinition
                {
                    OutputChannelId = "di.sensor",
                    TargetComponentId = "stage",
                    OnDelayMilliseconds = 10,
                    OffDelayMilliseconds = 5
                }
            },
            ["device.cylinder"] = new DeviceDefinition
            {
                Id = "device.cylinder",
                Name = "Cylinder",
                Kind = DeviceKind.Cylinder,
                Cylinder = new PneumaticCylinderDefinition
                {
                    ExtendCommandChannelId = "do.extend",
                    ExtendedSensorChannelId = "di.extended",
                    RetractedSensorChannelId = "di.retracted",
                    ExtendDurationMilliseconds = 100,
                    RetractDurationMilliseconds = 100,
                    ExtendedSensorDelayMilliseconds = 5,
                    RetractedSensorDelayMilliseconds = 5,
                    Stroke = 60
                }
            }
        };
}
