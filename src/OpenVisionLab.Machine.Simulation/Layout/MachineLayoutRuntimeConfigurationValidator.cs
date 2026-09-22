using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed record MachineLayoutRuntimeConfigurationValidationResult(
    ReadOnlyCollection<LayoutComponentRuntimeConfiguration> Components,
    ReadOnlyCollection<LoadLockRuntimeConfiguration> LoadLocks,
    ReadOnlyCollection<WaferHandlerRuntimeConfiguration> WaferHandlers,
    ReadOnlyCollection<InspectionSortRouterRuntimeConfiguration> InspectionSortRouters,
    ReadOnlyCollection<InspectionHandoffRuntimeConfiguration> InspectionHandoffs,
    ReadOnlyCollection<OhtHandoffRuntimeConfiguration> OhtHandoffs,
    ReadOnlyCollection<PrealignerRuntimeConfiguration> Prealigners);

/// <summary>
/// Validates cross-component runtime invariants and returns deterministic
/// ordinally ordered collections for the immutable layout configuration.
/// </summary>
internal static class MachineLayoutRuntimeConfigurationValidator
{
    internal static MachineLayoutRuntimeConfigurationValidationResult Validate(
        IEnumerable<LayoutComponentRuntimeConfiguration> components,
        IEnumerable<LoadLockRuntimeConfiguration>? loadLocks,
        IEnumerable<WaferHandlerRuntimeConfiguration>? waferHandlers,
        IEnumerable<InspectionSortRouterRuntimeConfiguration>? inspectionSortRouters,
        IEnumerable<InspectionHandoffRuntimeConfiguration>? inspectionHandoffs,
        IEnumerable<OhtHandoffRuntimeConfiguration>? ohtHandoffs,
        IEnumerable<PrealignerRuntimeConfiguration>? prealigners)
    {
        ArgumentNullException.ThrowIfNull(components);
        loadLocks ??= Array.Empty<LoadLockRuntimeConfiguration>();
        waferHandlers ??= Array.Empty<WaferHandlerRuntimeConfiguration>();
        inspectionSortRouters ??= Array.Empty<InspectionSortRouterRuntimeConfiguration>();
        inspectionHandoffs ??= Array.Empty<InspectionHandoffRuntimeConfiguration>();
        ohtHandoffs ??= Array.Empty<OhtHandoffRuntimeConfiguration>();
        prealigners ??= Array.Empty<PrealignerRuntimeConfiguration>();

        var componentsById = new SortedDictionary<string, LayoutComponentRuntimeConfiguration>(
            StringComparer.Ordinal);
        var simulationOwnedInputIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var actuatorCommandIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
            if (!componentsById.TryAdd(component.Id, component))
            {
                throw new ArgumentException(
                    $"Layout component id '{component.Id}' is duplicated.",
                    nameof(components));
            }

            if (component is DigitalSensorRuntimeConfiguration sensor)
            {
                ClaimResource(
                    simulationOwnedInputIds,
                    sensor.OutputChannelId,
                    sensor.Id,
                    "Digital-input channel",
                    "sensor",
                    nameof(components));
            }

            if (component is PneumaticCylinderRuntimeConfiguration cylinder)
            {
                ClaimResource(
                    actuatorCommandIds,
                    cylinder.ExtendCommandChannelId,
                    cylinder.Id,
                    "Digital-output channel",
                    "pneumatic cylinder",
                    nameof(components));
                ClaimResource(
                    simulationOwnedInputIds,
                    cylinder.ExtendedSensorChannelId,
                    cylinder.Id,
                    "Digital-input channel",
                    "pneumatic cylinder",
                    nameof(components));
                ClaimResource(
                    simulationOwnedInputIds,
                    cylinder.RetractedSensorChannelId,
                    cylinder.Id,
                    "Digital-input channel",
                    "pneumatic cylinder",
                    nameof(components));
            }

            if (component is ConveyorRuntimeConfiguration conveyor)
            {
                ClaimResource(
                    actuatorCommandIds,
                    conveyor.RunCommandChannelId,
                    conveyor.Id,
                    "Digital-output channel",
                    "conveyor",
                    nameof(components));
                ClaimResource(
                    actuatorCommandIds,
                    conveyor.ReverseCommandChannelId,
                    conveyor.Id,
                    "Digital-output channel",
                    "conveyor",
                    nameof(components));
            }
        }

        foreach (var sensor in componentsById.Values.OfType<DigitalSensorRuntimeConfiguration>())
        {
            if (!componentsById.ContainsKey(sensor.TargetComponentId))
            {
                throw new ArgumentException(
                    $"Sensor '{sensor.Id}' target component '{sensor.TargetComponentId}' was not found.",
                    nameof(components));
            }

            if (string.Equals(sensor.Id, sensor.TargetComponentId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Sensor '{sensor.Id}' cannot target itself.",
                    nameof(components));
            }
        }

        foreach (var workpiece in componentsById.Values.OfType<WorkpieceRuntimeConfiguration>())
        {
            if (!componentsById.TryGetValue(
                    workpiece.ConveyorComponentId,
                    out LayoutComponentRuntimeConfiguration? carrier)
                || carrier is not ConveyorRuntimeConfiguration conveyor)
            {
                throw new ArgumentException(
                    $"Workpiece '{workpiece.Id}' carrier '{workpiece.ConveyorComponentId}' must identify a conveyor.",
                    nameof(components));
            }

            ValidateWorkpiecePlacement(workpiece, conveyor);
        }

        var loadLocksById = new SortedDictionary<string, LoadLockRuntimeConfiguration>(
            StringComparer.Ordinal);
        var controlledDoorIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var loadLock in loadLocks)
        {
            ArgumentNullException.ThrowIfNull(loadLock);
            if (!loadLocksById.TryAdd(loadLock.Id, loadLock))
            {
                throw new ArgumentException($"Load-lock id '{loadLock.Id}' is duplicated.", nameof(loadLocks));
            }

            ValidateLoadLockDoor(loadLock, loadLock.OuterDoorComponentId, componentsById, controlledDoorIds);
            ValidateLoadLockDoor(loadLock, loadLock.InnerDoorComponentId, componentsById, controlledDoorIds);
            ClaimResource(
                actuatorCommandIds,
                loadLock.EvacuateCommandChannelId,
                loadLock.Id,
                "Digital-output channel",
                "load-lock",
                nameof(loadLocks));
            ClaimResource(
                actuatorCommandIds,
                loadLock.VentCommandChannelId,
                loadLock.Id,
                "Digital-output channel",
                "load-lock",
                nameof(loadLocks));
            ClaimResource(
                simulationOwnedInputIds,
                loadLock.VacuumReadySensorChannelId,
                loadLock.Id,
                "Digital-input channel",
                "load-lock",
                nameof(loadLocks));
            ClaimResource(
                simulationOwnedInputIds,
                loadLock.AtmosphereReadySensorChannelId,
                loadLock.Id,
                "Digital-input channel",
                "load-lock",
                nameof(loadLocks));
        }

        var waferHandlersById = new SortedDictionary<string, WaferHandlerRuntimeConfiguration>(StringComparer.Ordinal);
        var controlledWorkpieceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handler in waferHandlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!waferHandlersById.TryAdd(handler.Id, handler))
            {
                throw new ArgumentException($"Wafer-handler id '{handler.Id}' is duplicated.", nameof(waferHandlers));
            }

            if (!componentsById.TryGetValue(handler.WorkpieceComponentId, out var component)
                || component is not WorkpieceRuntimeConfiguration)
            {
                throw new ArgumentException($"Wafer-handler '{handler.Id}' workpiece '{handler.WorkpieceComponentId}' must identify an active workpiece.", nameof(waferHandlers));
            }

            if (controlledWorkpieceIds.TryGetValue(
                    handler.WorkpieceComponentId,
                    out string? existingHandler))
            {
                throw new ArgumentException(
                    $"Workpiece '{handler.WorkpieceComponentId}' cannot be controlled by more than one wafer-handler; " +
                    $"existing owner '{existingHandler}', conflicting owner '{handler.Id}'.",
                    nameof(waferHandlers));
            }

            controlledWorkpieceIds.Add(handler.WorkpieceComponentId, handler.Id);
            ClaimResource(
                actuatorCommandIds,
                handler.PickCommandChannelId,
                handler.Id,
                "Digital-output channel",
                "wafer-handler",
                nameof(waferHandlers));
            ClaimResource(
                actuatorCommandIds,
                handler.PlaceCommandChannelId,
                handler.Id,
                "Digital-output channel",
                "wafer-handler",
                nameof(waferHandlers));
            ClaimResource(
                simulationOwnedInputIds,
                handler.HoldingFeedbackChannelId,
                handler.Id,
                "Digital-input channel",
                "wafer-handler",
                nameof(waferHandlers));
            ClaimResource(
                simulationOwnedInputIds,
                handler.PlacedFeedbackChannelId,
                handler.Id,
                "Digital-input channel",
                "wafer-handler",
                nameof(waferHandlers));
        }

        var inspectionSortRoutersById = new SortedDictionary<string, InspectionSortRouterRuntimeConfiguration>(StringComparer.Ordinal);
        foreach (var sorter in inspectionSortRouters)
        {
            ArgumentNullException.ThrowIfNull(sorter);
            if (!inspectionSortRoutersById.TryAdd(sorter.Id, sorter))
            {
                throw new ArgumentException($"Inspection sorter id '{sorter.Id}' is duplicated.", nameof(inspectionSortRouters));
            }

            if (!componentsById.TryGetValue(sorter.PassConveyorComponentId, out var pass)
                || pass is not ConveyorRuntimeConfiguration passConveyor
                || !componentsById.TryGetValue(sorter.NgConveyorComponentId, out var ng)
                || ng is not ConveyorRuntimeConfiguration ngConveyor)
            {
                throw new ArgumentException($"Inspection sorter '{sorter.Id}' routes must identify two active conveyors.", nameof(inspectionSortRouters));
            }

            if (!string.Equals(
                    sorter.PassRunCommandChannelId,
                    passConveyor.RunCommandChannelId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    sorter.NgRunCommandChannelId,
                    ngConveyor.RunCommandChannelId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException($"Inspection sorter '{sorter.Id}' route commands must match the referenced conveyor Run channels.", nameof(inspectionSortRouters));
            }

            ClaimResource(
                simulationOwnedInputIds,
                sorter.PassRoutedFeedbackChannelId,
                sorter.Id,
                "Digital-input channel",
                "inspection sorter",
                nameof(inspectionSortRouters));
            ClaimResource(
                simulationOwnedInputIds,
                sorter.NgRoutedFeedbackChannelId,
                sorter.Id,
                "Digital-input channel",
                "inspection sorter",
                nameof(inspectionSortRouters));
        }

        var inspectionHandoffsById = new SortedDictionary<string, InspectionHandoffRuntimeConfiguration>(StringComparer.Ordinal);
        foreach (var handoff in inspectionHandoffs)
        {
            ArgumentNullException.ThrowIfNull(handoff);
            if (!inspectionHandoffsById.TryAdd(handoff.Id, handoff))
            {
                throw new ArgumentException($"Inspection handoff id '{handoff.Id}' is duplicated.", nameof(inspectionHandoffs));
            }

            ClaimResource(
                actuatorCommandIds,
                handoff.ResultAcceptedCommandChannelId,
                handoff.Id,
                "Digital-output channel",
                "inspection handoff",
                nameof(inspectionHandoffs));
            ClaimResource(
                simulationOwnedInputIds,
                handoff.InspectionReadyFeedbackChannelId,
                handoff.Id,
                "Digital-input channel",
                "inspection handoff",
                nameof(inspectionHandoffs));
            ClaimResource(
                simulationOwnedInputIds,
                handoff.InspectionCompleteFeedbackChannelId,
                handoff.Id,
                "Digital-input channel",
                "inspection handoff",
                nameof(inspectionHandoffs));
        }

        var ohtHandoffsById = new SortedDictionary<string, OhtHandoffRuntimeConfiguration>(StringComparer.Ordinal);
        var ohtConveyorIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handoff in ohtHandoffs)
        {
            ArgumentNullException.ThrowIfNull(handoff);
            if (!ohtHandoffsById.TryAdd(handoff.Id, handoff))
            {
                throw new ArgumentException($"OHT handoff id '{handoff.Id}' is duplicated.", nameof(ohtHandoffs));
            }

            if (!componentsById.TryGetValue(handoff.TransportConveyorComponentId, out var transport)
                || transport is not ConveyorRuntimeConfiguration conveyor
                || !string.Equals(handoff.ForwardCommandChannelId, conveyor.RunCommandChannelId, StringComparison.Ordinal)
                || !string.Equals(handoff.ReverseCommandChannelId, conveyor.ReverseCommandChannelId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"OHT handoff '{handoff.Id}' transport must identify one active conveyor and its command channels.", nameof(ohtHandoffs));
            }

            ClaimResource(
                ohtConveyorIds,
                handoff.TransportConveyorComponentId,
                handoff.Id,
                "Conveyor component",
                "OHT handoff",
                nameof(ohtHandoffs));
            ClaimResource(
                simulationOwnedInputIds,
                handoff.HandoffReadyFeedbackChannelId,
                handoff.Id,
                "Digital-input channel",
                "OHT handoff",
                nameof(ohtHandoffs));
            ClaimResource(
                simulationOwnedInputIds,
                handoff.CarrierTransferredFeedbackChannelId,
                handoff.Id,
                "Digital-input channel",
                "OHT handoff",
                nameof(ohtHandoffs));
        }

        var prealignersById = new SortedDictionary<string, PrealignerRuntimeConfiguration>(StringComparer.Ordinal);
        var prealignerStageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var prealignerClampIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prealigner in prealigners)
        {
            ArgumentNullException.ThrowIfNull(prealigner);
            if (!prealignersById.TryAdd(prealigner.Id, prealigner))
            {
                throw new ArgumentException($"Pre-aligner id '{prealigner.Id}' is duplicated.", nameof(prealigners));
            }

            if (!componentsById.TryGetValue(prealigner.RotaryStageComponentId, out var stage)
                || stage is not RotaryStageRuntimeConfiguration rotaryStage
                || !string.Equals(rotaryStage.AxisId, prealigner.RotaryAxisId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Pre-aligner '{prealigner.Id}' rotary stage must identify one active rotary stage and its axis.", nameof(prealigners));
            }

            if (!componentsById.TryGetValue(prealigner.ClampCylinderComponentId, out var clamp)
                || clamp is not PneumaticCylinderRuntimeConfiguration)
            {
                throw new ArgumentException($"Pre-aligner '{prealigner.Id}' clamp must identify one active pneumatic cylinder.", nameof(prealigners));
            }

            ClaimResource(
                prealignerStageIds,
                prealigner.RotaryStageComponentId,
                prealigner.Id,
                "Rotary-stage component",
                "pre-aligner",
                nameof(prealigners));
            ClaimResource(
                prealignerClampIds,
                prealigner.ClampCylinderComponentId,
                prealigner.Id,
                "Clamp-cylinder component",
                "pre-aligner",
                nameof(prealigners));
            ClaimResource(
                actuatorCommandIds,
                prealigner.AlignmentAcceptedCommandChannelId,
                prealigner.Id,
                "Digital-output channel",
                "pre-aligner",
                nameof(prealigners));
            ClaimResource(
                simulationOwnedInputIds,
                prealigner.AlignmentReadyFeedbackChannelId,
                prealigner.Id,
                "Digital-input channel",
                "pre-aligner",
                nameof(prealigners));
            ClaimResource(
                simulationOwnedInputIds,
                prealigner.AlignmentCompleteFeedbackChannelId,
                prealigner.Id,
                "Digital-input channel",
                "pre-aligner",
                nameof(prealigners));
        }

        return new(
            new ReadOnlyCollection<LayoutComponentRuntimeConfiguration>(
                componentsById.Values.ToArray()),
            new ReadOnlyCollection<LoadLockRuntimeConfiguration>(
                loadLocksById.Values.ToArray()),
            new ReadOnlyCollection<WaferHandlerRuntimeConfiguration>(
                waferHandlersById.Values.ToArray()),
            new ReadOnlyCollection<InspectionSortRouterRuntimeConfiguration>(
                inspectionSortRoutersById.Values.ToArray()),
            new ReadOnlyCollection<InspectionHandoffRuntimeConfiguration>(
                inspectionHandoffsById.Values.ToArray()),
            new ReadOnlyCollection<OhtHandoffRuntimeConfiguration>(
                ohtHandoffsById.Values.ToArray()),
            new ReadOnlyCollection<PrealignerRuntimeConfiguration>(
                prealignersById.Values.ToArray()));
    }

    private static void ValidateLoadLockDoor(
        LoadLockRuntimeConfiguration loadLock,
        string componentId,
        IReadOnlyDictionary<string, LayoutComponentRuntimeConfiguration> componentsById,
        IDictionary<string, string> controlledDoorIds)
    {
        if (!componentsById.TryGetValue(componentId, out var component)
            || component is not PneumaticCylinderRuntimeConfiguration)
        {
            throw new ArgumentException(
                $"Load-lock '{loadLock.Id}' door '{componentId}' must identify a pneumatic cylinder.");
        }

        ClaimResource(
            controlledDoorIds,
            componentId,
            loadLock.Id,
            "Pneumatic-cylinder component",
            "load-lock",
            nameof(controlledDoorIds));
    }

    private static void ClaimResource(
        IDictionary<string, string> owners,
        string resourceId,
        string ownerId,
        string resourceKind,
        string ownerKind,
        string parameterName)
    {
        if (owners.TryGetValue(resourceId, out string? existingOwner))
        {
            throw new ArgumentException(
                $"{resourceKind} '{resourceId}' is already owned by '{existingOwner}'; " +
                $"conflicting {ownerKind} owner '{ownerId}' is not allowed.",
                parameterName);
        }

        owners.Add(resourceId, ownerId);
    }

    private static void ValidateWorkpiecePlacement(
        WorkpieceRuntimeConfiguration workpiece,
        ConveyorRuntimeConfiguration conveyor)
    {
        const double tolerance = 1e-9;
        double angleDelta = Math.IEEERemainder(
            workpiece.BaseTransform.RotationDegrees - conveyor.BaseTransform.RotationDegrees,
            360d);
        if (Math.Abs(angleDelta) > tolerance)
        {
            throw new ArgumentException(
                $"Workpiece '{workpiece.Id}' must be aligned with conveyor '{conveyor.Id}'.",
                nameof(workpiece));
        }

        double radians = conveyor.BaseTransform.RotationDegrees * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double deltaX = workpiece.BaseTransform.X - conveyor.BaseTransform.X;
        double deltaY = workpiece.BaseTransform.Y - conveyor.BaseTransform.Y;
        double localX = (deltaX * cosine) + (deltaY * sine);
        double localY = (-deltaX * sine) + (deltaY * cosine);
        double maximumTravel = (conveyor.Size.Width - workpiece.Size.Width) / 2d;
        double maximumLateralOffset = (conveyor.Size.Height - workpiece.Size.Height) / 2d;
        if (maximumTravel < 0
            || maximumLateralOffset < 0
            || Math.Abs(localX) > maximumTravel + tolerance
            || Math.Abs(localY) > maximumLateralOffset + tolerance)
        {
            throw new ArgumentException(
                $"Workpiece '{workpiece.Id}' authored pose must fit inside conveyor '{conveyor.Id}'.",
                nameof(workpiece));
        }
    }
}
