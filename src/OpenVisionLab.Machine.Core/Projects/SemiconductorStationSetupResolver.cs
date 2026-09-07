using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.Machine.Core.Projects;

/// <summary>
/// Restores or derives the setup values used by the semiconductor station
/// template without mutating the authored project.
/// </summary>
internal sealed class SemiconductorStationSetupResolver
{
    internal SemiconductorStationSetupDefinition Resolve(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.SemiconductorStationSetup is { } persisted && IsValid(persisted))
        {
            return persisted with { };
        }

        var setup = new SemiconductorStationSetupDefinition();
        var layout = project.Layouts.FirstOrDefault(candidate => string.Equals(
                candidate.Id,
                project.Simulation.ActiveLayoutId,
                StringComparison.Ordinal))
            ?? (project.Layouts.Count == 1 ? project.Layouts[0] : null);
        if (!string.IsNullOrWhiteSpace(layout?.Name))
        {
            setup.StationName = layout.Name;
        }

        var workpiece = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Workpiece && device.Workpiece is not null);
        if (!string.IsNullOrWhiteSpace(workpiece?.Workpiece?.Type))
        {
            setup.WaferType = workpiece.Workpiece.Type;
        }

        var axis = project.Axes.FirstOrDefault(candidate => candidate.Kind == AxisKind.Linear);
        if (axis?.SoftLimitMin is { } minimum && axis.SoftLimitMax is { } maximum
            && double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum)
        {
            setup.AxisTravel = maximum - minimum;
        }

        var transport = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Conveyor && device.Conveyor is not null);
        if (transport?.Conveyor?.SpeedUnitsPerSecond is > 0 and var speed && double.IsFinite(speed))
        {
            setup.TransportSpeed = speed;
        }

        var sensorPositions = layout?.Components
            .Where(component => component.Kind == LayoutComponentKind.DigitalSensor)
            .Select(component => component.Transform.X)
            .Where(double.IsFinite)
            .Order()
            .Take(2)
            .ToArray() ?? [];
        if (sensorPositions.Length == 2 && sensorPositions[0] < sensorPositions[1])
        {
            setup.EntrySensorPosition = sensorPositions[0];
            setup.ProcessSensorPosition = sensorPositions[1];
        }

        var cylinder = project.Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Cylinder && device.Cylinder is not null);
        if (cylinder?.Cylinder?.ExtendDurationMilliseconds is > 0 and var duration)
        {
            setup.CylinderTravelTimeMilliseconds = duration;
        }
        return setup;
    }

    internal static bool IsValid(SemiconductorStationSetupDefinition setup) =>
        !string.IsNullOrWhiteSpace(setup.StationName)
        && !string.IsNullOrWhiteSpace(setup.WaferType)
        && double.IsFinite(setup.AxisTravel)
        && setup.AxisTravel > 0
        && double.IsFinite(setup.TransportSpeed)
        && setup.TransportSpeed > 0
        && double.IsFinite(setup.EntrySensorPosition)
        && double.IsFinite(setup.ProcessSensorPosition)
        && setup.EntrySensorPosition < setup.ProcessSensorPosition
        && setup.CylinderTravelTimeMilliseconds > 0;
}
