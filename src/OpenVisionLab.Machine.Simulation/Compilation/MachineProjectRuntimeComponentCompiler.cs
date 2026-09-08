using System.Globalization;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Compilation;

/// <summary>
/// Converts active layout component definitions into runtime component records.
/// </summary>
internal sealed class MachineProjectRuntimeComponentCompiler
{
    private readonly FixedStepDelayConverter _delayConverter;

    internal MachineProjectRuntimeComponentCompiler(FixedStepDelayConverter delayConverter)
    {
        ArgumentNullException.ThrowIfNull(delayConverter);
        _delayConverter = delayConverter;
    }

    internal IReadOnlyList<LayoutComponentRuntimeConfiguration> Compile(
        MachineLayoutDefinition activeLayout,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, DeviceDefinition> devicesById,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        ArgumentNullException.ThrowIfNull(activeLayout);
        ArgumentNullException.ThrowIfNull(axesById);
        ArgumentNullException.ThrowIfNull(devicesById);
        ArgumentNullException.ThrowIfNull(errors);

        var activeComponentIds = activeLayout.Components
            .Select(component => component.Id)
            .ToHashSet(StringComparer.Ordinal);
        var runtimeComponents = new List<LayoutComponentRuntimeConfiguration>();

        foreach (LayoutComponentDefinition component in activeLayout.Components)
        {
            var transform = new LayoutRuntimeTransform(
                component.Transform.X,
                component.Transform.Y,
                component.Transform.RotationDegrees);
            var size = new LayoutRuntimeSize(component.Size.Width, component.Size.Height);

            switch (component.Kind)
            {
                case LayoutComponentKind.MachineFrame:
                    runtimeComponents.Add(new MachineFrameRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.LinearStage:
                    VirtualAxisDefinition axis = axesById[component.BehaviorBindingId!];
                    runtimeComponents.Add(new LinearStageRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        axis.Id,
                        axis.HomePosition,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.RotaryStage:
                    VirtualAxisDefinition rotaryAxis = axesById[component.BehaviorBindingId!];
                    runtimeComponents.Add(new RotaryStageRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        rotaryAxis.Id,
                        rotaryAxis.HomePosition,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.DigitalSensor:
                    DeviceDefinition device = devicesById[component.BehaviorBindingId!];
                    DigitalSensorDefinition sensor = device.Sensor!;
                    if (!activeComponentIds.Contains(sensor.TargetComponentId))
                    {
                        errors.Add(Error(
                            MachineProjectRuntimeCompilationErrorCode.LayoutTargetOutsideActiveLayout,
                            component.Id,
                            $"Sensor '{component.Id}' target '{sensor.TargetComponentId}' is not part of active layout '{activeLayout.Id}'."));
                        break;
                    }

                    bool onValid = _delayConverter.TryConvertDelayToTicks(
                        sensor.OnDelayMilliseconds,
                        allowZero: true,
                        out int onDelayTicks);
                    bool offValid = _delayConverter.TryConvertDelayToTicks(
                        sensor.OffDelayMilliseconds,
                        allowZero: true,
                        out int offDelayTicks);
                    if (!onValid || !offValid)
                    {
                        errors.Add(Error(
                            MachineProjectRuntimeCompilationErrorCode.SensorDelayInvalid,
                            component.Id,
                            $"Digital sensor '{component.Id}' on/off delays must be zero or exact multiples of {_delayConverter.FixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
                        break;
                    }

                    runtimeComponents.Add(new DigitalSensorRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        sensor.OutputChannelId,
                        sensor.TargetComponentId,
                        onDelayTicks,
                        offDelayTicks,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.PneumaticCylinder:
                    DeviceDefinition cylinderDevice = devicesById[component.BehaviorBindingId!];
                    PneumaticCylinderDefinition cylinder = cylinderDevice.Cylinder!;
                    bool extendValid = _delayConverter.TryConvertDelayToTicks(
                        cylinder.ExtendDurationMilliseconds,
                        allowZero: false,
                        out int extendDurationTicks);
                    bool retractValid = _delayConverter.TryConvertDelayToTicks(
                        cylinder.RetractDurationMilliseconds,
                        allowZero: false,
                        out int retractDurationTicks);
                    bool extendedDelayValid = _delayConverter.TryConvertDelayToTicks(
                        cylinder.ExtendedSensorDelayMilliseconds,
                        allowZero: true,
                        out int extendedSensorDelayTicks);
                    bool retractedDelayValid = _delayConverter.TryConvertDelayToTicks(
                        cylinder.RetractedSensorDelayMilliseconds,
                        allowZero: true,
                        out int retractedSensorDelayTicks);
                    if (!extendValid || !retractValid || !extendedDelayValid || !retractedDelayValid)
                    {
                        errors.Add(Error(
                            MachineProjectRuntimeCompilationErrorCode.CylinderTimingInvalid,
                            component.Id,
                            $"Pneumatic cylinder '{component.Id}' durations must be positive and " +
                            $"sensor delays must be non-negative exact multiples of " +
                            $"{_delayConverter.FixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms."));
                        break;
                    }

                    runtimeComponents.Add(new PneumaticCylinderRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        cylinder.ExtendCommandChannelId,
                        cylinder.ExtendedSensorChannelId,
                        cylinder.RetractedSensorChannelId,
                        extendDurationTicks,
                        retractDurationTicks,
                        extendedSensorDelayTicks,
                        retractedSensorDelayTicks,
                        cylinder.Stroke,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.Conveyor:
                    DeviceDefinition conveyorDevice = devicesById[component.BehaviorBindingId!];
                    ConveyorDefinition conveyor = conveyorDevice.Conveyor!;
                    runtimeComponents.Add(new ConveyorRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        conveyor.RunCommandChannelId,
                        conveyor.ReverseCommandChannelId,
                        conveyor.SpeedUnitsPerSecond,
                        _delayConverter.FixedStep.TotalSeconds,
                        transform,
                        size));
                    break;

                case LayoutComponentKind.Workpiece:
                    DeviceDefinition workpieceDevice = devicesById[component.BehaviorBindingId!];
                    WorkpieceDefinition workpiece = workpieceDevice.Workpiece!;
                    runtimeComponents.Add(new WorkpieceRuntimeConfiguration(
                        component.Id,
                        component.Name,
                        workpiece.Type,
                        workpiece.ConveyorComponentId,
                        workpiece.InspectionState,
                        transform,
                        size));
                    break;
            }
        }

        return runtimeComponents;
    }

    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
