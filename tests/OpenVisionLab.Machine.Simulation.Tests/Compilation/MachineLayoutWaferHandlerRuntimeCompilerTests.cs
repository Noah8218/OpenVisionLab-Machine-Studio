using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineLayoutWaferHandlerRuntimeCompilerTests
{
    [Fact]
    public void Compile_ValidDefinition_ReturnsTypedRuntimeConfiguration()
    {
        var errors = new List<MachineProjectRuntimeCompilationError>();

        IReadOnlyList<WaferHandlerRuntimeConfiguration> result =
            new MachineLayoutWaferHandlerRuntimeCompiler().Compile(
                new[] { CreateDevice() },
                Components(),
                Axes(),
                ChannelKinds(),
                errors);

        var handler = Assert.Single(result);
        Assert.Empty(errors);
        Assert.Equal("handler", handler.Id);
        Assert.Equal("axis.horizontal", handler.HorizontalAxisId);
        Assert.Equal("axis.vertical", handler.VerticalAxisId);
        Assert.Equal("wafer", handler.WorkpieceComponentId);
        Assert.Equal(140, handler.PlaceHorizontalPosition);
        Assert.Equal(260, handler.PlaceVerticalPosition);
    }

    [Fact]
    public void Compile_MissingWorkpiece_ReportsTypedError()
    {
        DeviceDefinition device = CreateDevice();
        device.WaferHandler!.WorkpieceComponentId = "missing-wafer";
        var errors = new List<MachineProjectRuntimeCompilationError>();

        IReadOnlyList<WaferHandlerRuntimeConfiguration> result =
            new MachineLayoutWaferHandlerRuntimeCompiler().Compile(
                new[] { device },
                Components(),
                Axes(),
                ChannelKinds(),
                errors);

        Assert.Empty(result);
        var error = Assert.Single(errors);
        Assert.Equal(MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid, error.Code);
        Assert.Equal("handler", error.TargetId);
        Assert.Contains("workpiece in the active layout", error.Message, StringComparison.Ordinal);
    }

    private static DeviceDefinition CreateDevice() => new()
    {
        Id = "handler",
        Name = "Handler",
        Kind = DeviceKind.Handler,
        WaferHandler = new WaferHandlerDefinition
        {
            HorizontalAxisId = "axis.horizontal",
            VerticalAxisId = "axis.vertical",
            WorkpieceComponentId = "wafer",
            SourcePresentSensorChannelId = "di.source",
            GateOpenSensorChannelId = "di.gate",
            PickCommandChannelId = "do.pick",
            PlaceCommandChannelId = "do.place",
            HoldingFeedbackChannelId = "di.holding",
            PlacedFeedbackChannelId = "di.placed",
            PickHorizontalPosition = 0,
            PickVerticalPosition = 260,
            PlaceHorizontalPosition = 140,
            PlaceVerticalPosition = 260
        }
    };

    private static IReadOnlyCollection<LayoutComponentRuntimeConfiguration> Components() =>
        new LayoutComponentRuntimeConfiguration[]
        {
            new WorkpieceRuntimeConfiguration(
                "wafer",
                "Wafer",
                "Test wafer",
                "transport",
                WorkpieceInspectionState.Pending,
                new LayoutRuntimeTransform(0, 0),
                new LayoutRuntimeSize(20, 20))
        };

    private static IReadOnlyDictionary<string, VirtualAxisDefinition> Axes() =>
        new Dictionary<string, VirtualAxisDefinition>(StringComparer.Ordinal)
        {
            ["axis.horizontal"] = new()
            {
                Id = "axis.horizontal",
                Name = "Horizontal",
                Kind = AxisKind.Linear,
                SoftLimitMin = 0,
                SoftLimitMax = 200
            },
            ["axis.vertical"] = new()
            {
                Id = "axis.vertical",
                Name = "Vertical",
                Kind = AxisKind.Linear,
                SoftLimitMin = 0,
                SoftLimitMax = 300
            }
        };

    private static IReadOnlyDictionary<string, ChannelKind> ChannelKinds() =>
        new Dictionary<string, ChannelKind>(StringComparer.Ordinal)
        {
            ["di.source"] = ChannelKind.DigitalInput,
            ["di.gate"] = ChannelKind.DigitalInput,
            ["do.pick"] = ChannelKind.DigitalOutput,
            ["do.place"] = ChannelKind.DigitalOutput,
            ["di.holding"] = ChannelKind.DigitalInput,
            ["di.placed"] = ChannelKind.DigitalInput
        };
}
