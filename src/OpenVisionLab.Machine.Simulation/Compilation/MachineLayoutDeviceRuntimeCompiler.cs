using System.Globalization;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineLayoutDeviceRuntimeCompiler
{
    private readonly FixedStepDelayConverter _delayConverter;
    private readonly MachineLayoutInspectionRuntimeCompiler _inspectionRuntimeCompiler = new();
    private readonly MachineLayoutWaferHandlerRuntimeCompiler _waferHandlerRuntimeCompiler = new();

    internal MachineLayoutDeviceRuntimeCompiler(FixedStepDelayConverter delayConverter)
    {
        ArgumentNullException.ThrowIfNull(delayConverter);
        _delayConverter = delayConverter;
    }

    internal MachineLayoutRuntimeConfiguration? Compile(
        string layoutId,
        string layoutName,
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        IReadOnlyList<LoadLockRuntimeConfiguration> loadLocks = BuildLoadLocks(
            devices,
            runtimeComponents,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<WaferHandlerRuntimeConfiguration> waferHandlers = _waferHandlerRuntimeCompiler.Compile(
            devices,
            runtimeComponents,
            axesById,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<InspectionSortRouterRuntimeConfiguration> inspectionSortRouters =
            _inspectionRuntimeCompiler.BuildSortRouters(
                devices,
                runtimeComponents,
                channelKinds,
                errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<InspectionHandoffRuntimeConfiguration> inspectionHandoffs =
            _inspectionRuntimeCompiler.BuildHandoffs(
                devices,
                channelKinds,
                errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<OhtHandoffRuntimeConfiguration> ohtHandoffs = BuildOhtHandoffs(
            devices,
            runtimeComponents,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.OhtHandoffConfigurationInvalid))
        {
            return null;
        }

        IReadOnlyList<PrealignerRuntimeConfiguration> prealigners = BuildPrealigners(
            devices,
            runtimeComponents,
            axesById,
            channelKinds,
            errors);
        if (errors.Any(error =>
                error.Code == MachineProjectRuntimeCompilationErrorCode.PrealignerConfigurationInvalid))
        {
            return null;
        }

        try
        {
            return new MachineLayoutRuntimeConfiguration(
                layoutId,
                layoutName,
                runtimeComponents,
                loadLocks,
                waferHandlers,
                inspectionSortRouters,
                inspectionHandoffs,
                ohtHandoffs,
                prealigners);
        }
        catch (ArgumentException exception)
        {
            errors.Add(Error(
                MachineProjectRuntimeCompilationErrorCode.LayoutRuntimeInvalid,
                layoutId,
                $"Active layout runtime configuration is invalid: {exception.Message}"));
            return null;
        }
    }

    private IReadOnlyList<LoadLockRuntimeConfiguration> BuildLoadLocks(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var componentsById = runtimeComponents.ToDictionary(
            component => component.Id,
            StringComparer.Ordinal);
        var loadLocks = new List<LoadLockRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.LoadLock)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            LoadLockDefinition? definition = device.LoadLock;
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.loadLock" : device.Id;
            if (definition is null)
            {
                AddLoadLockError(errors, targetId, "Load-lock settings are required.");
                continue;
            }

            if (!IsCylinder(definition.OuterDoorComponentId, componentsById)
                || !IsCylinder(definition.InnerDoorComponentId, componentsById)
                || string.Equals(
                    definition.OuterDoorComponentId,
                    definition.InnerDoorComponentId,
                    StringComparison.Ordinal))
            {
                AddLoadLockError(
                    errors,
                    targetId,
                    "Load-lock outer and inner door ids must identify two distinct pneumatic cylinders in the active layout.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(
                    definition.EvacuateCommandChannelId,
                    ChannelKind.DigitalOutput,
                    channelKinds)
                || !HasChannelKind(
                    definition.VentCommandChannelId,
                    ChannelKind.DigitalOutput,
                    channelKinds)
                || !HasChannelKind(
                    definition.VacuumReadySensorChannelId,
                    ChannelKind.DigitalInput,
                    channelKinds)
                || !HasChannelKind(
                    definition.AtmosphereReadySensorChannelId,
                    ChannelKind.DigitalInput,
                    channelKinds))
            {
                AddLoadLockError(
                    errors,
                    targetId,
                    "Load-lock evacuate/vent channels must be DigitalOutput and vacuum/atmosphere feedback channels must be DigitalInput.");
                continue;
            }

            bool pumpDownValid = _delayConverter.TryConvertDelayToTicks(
                definition.PumpDownDurationMilliseconds,
                allowZero: false,
                out int pumpDownDurationTicks);
            bool ventValid = _delayConverter.TryConvertDelayToTicks(
                definition.VentDurationMilliseconds,
                allowZero: false,
                out int ventDurationTicks);
            if (!pumpDownValid || !ventValid)
            {
                AddLoadLockError(
                    errors,
                    targetId,
                    $"Load-lock pump-down and vent durations must be positive exact multiples of {_delayConverter.FixedStep.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms.");
                continue;
            }

            try
            {
                loadLocks.Add(new LoadLockRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.OuterDoorComponentId,
                    definition.InnerDoorComponentId,
                    definition.EvacuateCommandChannelId,
                    definition.VentCommandChannelId,
                    definition.VacuumReadySensorChannelId,
                    definition.AtmosphereReadySensorChannelId,
                    pumpDownDurationTicks,
                    ventDurationTicks));
            }
            catch (ArgumentException exception)
            {
                AddLoadLockError(errors, targetId, exception.Message);
            }
        }

        return loadLocks;
    }

    private static bool IsCylinder(
        string componentId,
        IReadOnlyDictionary<string, LayoutComponentRuntimeConfiguration> componentsById) =>
        !string.IsNullOrWhiteSpace(componentId)
        && componentsById.TryGetValue(componentId, out var component)
        && component is PneumaticCylinderRuntimeConfiguration;

    private static IReadOnlyList<PrealignerRuntimeConfiguration> BuildPrealigners(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var componentsById = runtimeComponents.ToDictionary(component => component.Id, StringComparer.Ordinal);
        var prealigners = new List<PrealignerRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Prealigner)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.prealigner" : device.Id;
            PrealignerDefinition? definition = device.Prealigner;
            if (definition is null)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner settings are required.");
                continue;
            }

            if (!componentsById.TryGetValue(definition.RotaryStageComponentId, out var stageComponent)
                || stageComponent is not RotaryStageRuntimeConfiguration rotaryStage
                || !axesById.TryGetValue(rotaryStage.AxisId, out VirtualAxisDefinition? rotaryAxis)
                || rotaryAxis.Kind != AxisKind.Rotary)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner stage must identify an active rotary stage bound to a Rotary axis.");
                continue;
            }

            if (!componentsById.TryGetValue(definition.ClampCylinderComponentId, out var clamp)
                || clamp is not PneumaticCylinderRuntimeConfiguration)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner clamp must identify an active pneumatic cylinder.");
                continue;
            }

            if (!double.IsFinite(definition.AlignmentTargetDegrees)
                || definition.AlignmentTargetDegrees < rotaryAxis.SoftLimitMin
                || definition.AlignmentTargetDegrees > rotaryAxis.SoftLimitMax
                || !double.IsFinite(definition.AlignmentToleranceDegrees)
                || definition.AlignmentToleranceDegrees <= 0)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner target must be finite and within rotary-axis limits, with a positive finite tolerance.");
                continue;
            }

            string[] inputIds =
            {
                definition.WaferPresentSensorChannelId,
                definition.AlignmentReadyFeedbackChannelId,
                definition.AlignmentCompleteFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || !HasChannelKind(definition.AlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || inputIds.Append(definition.AlignmentAcceptedCommandChannelId).Distinct(StringComparer.Ordinal).Count() != 4)
            {
                AddPrealignerError(errors, targetId, "Pre-aligner requires three distinct DigitalInput channels and one distinct DigitalOutput accept command.");
                continue;
            }

            try
            {
                prealigners.Add(new PrealignerRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    rotaryStage.Id,
                    rotaryStage.AxisId,
                    definition.ClampCylinderComponentId,
                    definition.WaferPresentSensorChannelId,
                    definition.AlignmentAcceptedCommandChannelId,
                    definition.AlignmentReadyFeedbackChannelId,
                    definition.AlignmentCompleteFeedbackChannelId,
                    definition.AlignmentTargetDegrees,
                    definition.AlignmentToleranceDegrees));
            }
            catch (ArgumentException exception)
            {
                AddPrealignerError(errors, targetId, exception.Message);
            }
        }

        return prealigners;
    }

    private static void AddPrealignerError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.PrealignerConfigurationInvalid,
            targetId,
            message));

    private static IReadOnlyList<OhtHandoffRuntimeConfiguration> BuildOhtHandoffs(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var conveyorsById = runtimeComponents
            .OfType<ConveyorRuntimeConfiguration>()
            .ToDictionary(conveyor => conveyor.Id, StringComparer.Ordinal);
        var handoffs = new List<OhtHandoffRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Oht)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.ohtHandoff" : device.Id;
            OhtHandoffDefinition? definition = device.OhtHandoff;
            if (definition is null)
            {
                AddOhtHandoffError(errors, targetId, "OHT handoff settings are required.");
                continue;
            }

            if (!conveyorsById.TryGetValue(definition.TransportConveyorComponentId, out var conveyor))
            {
                AddOhtHandoffError(errors, targetId, "OHT handoff transport must identify a conveyor in the active layout.");
                continue;
            }

            string[] inputIds =
            {
                definition.RouteAvailableSensorChannelId,
                definition.VehicleDockedSensorChannelId,
                definition.LoadPortReadySensorChannelId,
                definition.CarrierReceivedSensorChannelId,
                definition.HandoffReadyFeedbackChannelId,
                definition.CarrierTransferredFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || inputIds.Distinct(StringComparer.Ordinal).Count() != inputIds.Length)
            {
                AddOhtHandoffError(errors, targetId, "OHT handoff conditions and feedback must be six distinct DigitalInput channels.");
                continue;
            }

            try
            {
                handoffs.Add(new OhtHandoffRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    conveyor.Id,
                    conveyor.RunCommandChannelId,
                    conveyor.ReverseCommandChannelId,
                    definition.RouteAvailableSensorChannelId,
                    definition.VehicleDockedSensorChannelId,
                    definition.LoadPortReadySensorChannelId,
                    definition.CarrierReceivedSensorChannelId,
                    definition.HandoffReadyFeedbackChannelId,
                    definition.CarrierTransferredFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddOhtHandoffError(errors, targetId, exception.Message);
            }
        }

        return handoffs;
    }

    private static void AddOhtHandoffError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.OhtHandoffConfigurationInvalid,
            targetId,
            message));

    private static bool HasChannelKind(
        string channelId,
        ChannelKind expectedKind,
        IReadOnlyDictionary<string, ChannelKind> channelKinds) =>
        !string.IsNullOrWhiteSpace(channelId)
        && channelKinds.TryGetValue(channelId, out ChannelKind kind)
        && kind == expectedKind;

    private static void AddLoadLockError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.LoadLockConfigurationInvalid,
            targetId,
            message));



    private static MachineProjectRuntimeCompilationError Error(
        MachineProjectRuntimeCompilationErrorCode code,
        string? targetId,
        string message) =>
        new(code, targetId, message);
}
