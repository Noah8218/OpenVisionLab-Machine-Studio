using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineLayoutRuntimeConfigurationValidatorTests
{
    [Fact]
    public void ValidateReturnsOrdinallyOrderedReadOnlyCollections()
    {
        var later = Conveyor("conveyor-2", "do.conveyor-2.run", "do.conveyor-2.reverse");
        var earlier = Conveyor("conveyor-1", "do.conveyor-1.run", "do.conveyor-1.reverse");

        var result = MachineLayoutRuntimeConfigurationValidator.Validate(
            new[] { later, earlier },
            loadLocks: null,
            waferHandlers: null,
            inspectionSortRouters: null,
            inspectionHandoffs: null,
            ohtHandoffs: null,
            prealigners: null);

        Assert.Equal(new[] { "conveyor-1", "conveyor-2" }, result.Components.Select(component => component.Id));
        Assert.Empty(result.LoadLocks);
        Assert.Empty(result.WaferHandlers);
        Assert.Empty(result.InspectionSortRouters);
        Assert.Empty(result.InspectionHandoffs);
        Assert.Empty(result.OhtHandoffs);
        Assert.Empty(result.Prealigners);
    }

    [Fact]
    public void ValidateRejectsSensorTargetThatIsNotInTheSameLayout()
    {
        var sensor = new DigitalSensorRuntimeConfiguration(
            "sensor-1",
            "Sensor",
            "di.sensor-1",
            "missing",
            0,
            0,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(2, 20));

        var exception = Assert.Throws<ArgumentException>(() => MachineLayoutRuntimeConfigurationValidator.Validate(
            new LayoutComponentRuntimeConfiguration[] { sensor },
            null,
            null,
            null,
            null,
            null,
            null));

        Assert.Equal("components", exception.ParamName);
        Assert.Contains("target component 'missing' was not found", exception.Message, StringComparison.Ordinal);
    }

    private static ConveyorRuntimeConfiguration Conveyor(
        string id,
        string runChannelId,
        string reverseChannelId) =>
        new(
            id,
            id,
            runChannelId,
            reverseChannelId,
            10,
            0.1,
            new LayoutRuntimeTransform(0, 0),
            new LayoutRuntimeSize(100, 40));
}
