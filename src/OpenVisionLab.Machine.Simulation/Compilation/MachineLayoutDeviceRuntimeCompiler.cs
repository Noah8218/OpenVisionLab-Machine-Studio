using System.Globalization;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.Machine.Simulation.Compilation;

internal sealed class MachineLayoutDeviceRuntimeCompiler
{
    private readonly FixedStepDelayConverter _delayConverter;

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

        IReadOnlyList<WaferHandlerRuntimeConfiguration> waferHandlers = BuildWaferHandlers(
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
            BuildInspectionSortRouters(
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
            BuildInspectionHandoffs(
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

    private static IReadOnlyList<WaferHandlerRuntimeConfiguration> BuildWaferHandlers(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, VirtualAxisDefinition> axesById,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        var workpieceIds = runtimeComponents
            .OfType<WorkpieceRuntimeConfiguration>()
            .Select(workpiece => workpiece.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handlers = new List<WaferHandlerRuntimeConfiguration>();

        foreach (DeviceDefinition device in devices
                     .Where(device => device.Kind == DeviceKind.Handler)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.waferHandler" : device.Id;
            WaferHandlerDefinition? definition = device.WaferHandler;
            if (definition is null)
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler settings are required.");
                continue;
            }

            if (!axesById.TryGetValue(definition.HorizontalAxisId, out VirtualAxisDefinition? horizontal)
                || !axesById.TryGetValue(definition.VerticalAxisId, out VirtualAxisDefinition? vertical)
                || horizontal.Kind != AxisKind.Linear
                || vertical.Kind != AxisKind.Linear
                || string.Equals(horizontal.Id, vertical.Id, StringComparison.Ordinal))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler axes must identify two distinct configured linear axes.");
                continue;
            }

            if (!workpieceIds.Contains(definition.WorkpieceComponentId))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler workpiece must identify a workpiece in the active layout.");
                continue;
            }

            if (!PositionWithin(horizontal, definition.PickHorizontalPosition)
                || !PositionWithin(vertical, definition.PickVerticalPosition)
                || !PositionWithin(horizontal, definition.PlaceHorizontalPosition)
                || !PositionWithin(vertical, definition.PlaceVerticalPosition))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler pick and place positions must be finite and within their axis soft limits.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(definition.SourcePresentSensorChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.GateOpenSensorChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.PickCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || !HasChannelKind(definition.PlaceCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || !HasChannelKind(definition.HoldingFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.PlacedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds))
            {
                AddWaferHandlerError(errors, targetId, "Wafer-handler conditions/feedback must be DigitalInput and pick/place commands must be DigitalOutput.");
                continue;
            }

            try
            {
                handlers.Add(new WaferHandlerRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.HorizontalAxisId,
                    definition.VerticalAxisId,
                    definition.WorkpieceComponentId,
                    definition.SourcePresentSensorChannelId,
                    definition.GateOpenSensorChannelId,
                    definition.PickCommandChannelId,
                    definition.PlaceCommandChannelId,
                    definition.HoldingFeedbackChannelId,
                    definition.PlacedFeedbackChannelId,
                    definition.PickHorizontalPosition,
                    definition.PickVerticalPosition,
                    definition.PlaceHorizontalPosition,
                    definition.PlaceVerticalPosition));
            }
            catch (ArgumentException exception)
            {
                AddWaferHandlerError(errors, targetId, exception.Message);
            }
        }

        return handlers;
    }

    private static bool PositionWithin(VirtualAxisDefinition axis, double position) =>
        double.IsFinite(position)
        && axis.SoftLimitMin.HasValue
        && axis.SoftLimitMax.HasValue
        && position >= axis.SoftLimitMin.Value
        && position <= axis.SoftLimitMax.Value;

    private static IReadOnlyList<InspectionSortRouterRuntimeConfiguration> BuildInspectionSortRouters(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyCollection<LayoutComponentRuntimeConfiguration> runtimeComponents,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        DeviceDefinition[] deviceArray = devices.ToArray();
        var cameraIds = deviceArray
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var conveyorsById = runtimeComponents
            .OfType<ConveyorRuntimeConfiguration>()
            .ToDictionary(conveyor => conveyor.Id, StringComparer.Ordinal);
        var sorters = new List<InspectionSortRouterRuntimeConfiguration>();

        foreach (DeviceDefinition device in deviceArray
                     .Where(device => device.Kind == DeviceKind.Sorter)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.inspectionSorter" : device.Id;
            InspectionSortRouterDefinition? definition = device.InspectionSortRouter;
            if (definition is null)
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection-sorter settings are required.");
                continue;
            }

            if (!cameraIds.Contains(definition.CameraId))
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection sorter camera must identify a configured virtual camera.");
                continue;
            }

            if (!conveyorsById.TryGetValue(definition.PassConveyorComponentId, out var passConveyor)
                || !conveyorsById.TryGetValue(definition.NgConveyorComponentId, out var ngConveyor)
                || string.Equals(passConveyor.Id, ngConveyor.Id, StringComparison.Ordinal))
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection sorter routes must identify two distinct conveyors in the active layout.");
                continue;
            }

            if (channelKinds is null
                || !HasChannelKind(definition.PassRoutedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || !HasChannelKind(definition.NgRoutedFeedbackChannelId, ChannelKind.DigitalInput, channelKinds)
                || string.Equals(
                    definition.PassRoutedFeedbackChannelId,
                    definition.NgRoutedFeedbackChannelId,
                    StringComparison.Ordinal))
            {
                AddInspectionSortRouterError(errors, targetId, "Inspection sorter route feedback channels must be two distinct DigitalInput channels.");
                continue;
            }

            try
            {
                sorters.Add(new InspectionSortRouterRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.CameraId,
                    passConveyor.Id,
                    ngConveyor.Id,
                    passConveyor.RunCommandChannelId,
                    ngConveyor.RunCommandChannelId,
                    definition.PassRoutedFeedbackChannelId,
                    definition.NgRoutedFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddInspectionSortRouterError(errors, targetId, exception.Message);
            }
        }

        return sorters;
    }

    private static void AddInspectionSortRouterError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.InspectionSortRouterConfigurationInvalid,
            targetId,
            message));

    private static IReadOnlyList<InspectionHandoffRuntimeConfiguration> BuildInspectionHandoffs(
        IEnumerable<DeviceDefinition> devices,
        IReadOnlyDictionary<string, ChannelKind>? channelKinds,
        ICollection<MachineProjectRuntimeCompilationError> errors)
    {
        DeviceDefinition[] deviceArray = devices.ToArray();
        var cameraIds = deviceArray
            .Where(device => device is { Kind: DeviceKind.Camera, Camera: not null })
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var handoffs = new List<InspectionHandoffRuntimeConfiguration>();

        foreach (DeviceDefinition device in deviceArray
                     .Where(device => device.Kind == DeviceKind.Inspection)
                     .OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            string targetId = string.IsNullOrWhiteSpace(device.Id) ? "devices.inspectionHandoff" : device.Id;
            InspectionHandoffDefinition? definition = device.InspectionHandoff;
            if (definition is null)
            {
                AddInspectionHandoffError(errors, targetId, "Inspection-handoff settings are required.");
                continue;
            }

            if (!cameraIds.Contains(definition.CameraId))
            {
                AddInspectionHandoffError(errors, targetId, "Inspection handoff camera must identify a configured virtual camera.");
                continue;
            }

            string[] inputIds =
            {
                definition.InspectionPositionSensorChannelId,
                definition.InspectionReadyFeedbackChannelId,
                definition.InspectionCompleteFeedbackChannelId
            };
            if (channelKinds is null
                || inputIds.Any(channelId => !HasChannelKind(channelId, ChannelKind.DigitalInput, channelKinds))
                || !HasChannelKind(definition.ResultAcceptedCommandChannelId, ChannelKind.DigitalOutput, channelKinds)
                || inputIds.Append(definition.ResultAcceptedCommandChannelId).Distinct(StringComparer.Ordinal).Count() != 4)
            {
                AddInspectionHandoffError(errors, targetId, "Inspection handoff requires three distinct DigitalInput channels and one distinct DigitalOutput result-accepted command.");
                continue;
            }

            try
            {
                handoffs.Add(new InspectionHandoffRuntimeConfiguration(
                    device.Id,
                    device.Name,
                    definition.CameraId,
                    definition.InspectionPositionSensorChannelId,
                    definition.ResultAcceptedCommandChannelId,
                    definition.InspectionReadyFeedbackChannelId,
                    definition.InspectionCompleteFeedbackChannelId));
            }
            catch (ArgumentException exception)
            {
                AddInspectionHandoffError(errors, targetId, exception.Message);
            }
        }

        return handoffs;
    }

    private static void AddInspectionHandoffError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.InspectionHandoffConfigurationInvalid,
            targetId,
            message));

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

    private static void AddWaferHandlerError(
        ICollection<MachineProjectRuntimeCompilationError> errors,
        string targetId,
        string message) =>
        errors.Add(Error(
            MachineProjectRuntimeCompilationErrorCode.WaferHandlerConfigurationInvalid,
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
