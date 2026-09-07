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
        var simulationOwnedInputIds = new HashSet<string>(StringComparer.Ordinal);
        var actuatorCommandIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var component in components)
        {
            ArgumentNullException.ThrowIfNull(component);
            if (!componentsById.TryAdd(component.Id, component))
            {
                throw new ArgumentException(
                    $"Layout component id '{component.Id}' is duplicated.",
                    nameof(components));
            }

            if (component is DigitalSensorRuntimeConfiguration sensor &&
                !simulationOwnedInputIds.Add(sensor.OutputChannelId))
            {
                throw new ArgumentException(
                    $"Digital-input channel '{sensor.OutputChannelId}' is owned by more than one sensor.",
                    nameof(components));
            }

            if (component is PneumaticCylinderRuntimeConfiguration cylinder)
            {
                if (!actuatorCommandIds.Add(cylinder.ExtendCommandChannelId))
                {
                    throw new ArgumentException(
                        $"Digital-output channel '{cylinder.ExtendCommandChannelId}' commands more than one cylinder.",
                        nameof(components));
                }

                if (!simulationOwnedInputIds.Add(cylinder.ExtendedSensorChannelId)
                    || !simulationOwnedInputIds.Add(cylinder.RetractedSensorChannelId))
                {
                    throw new ArgumentException(
                        $"Cylinder '{cylinder.Id}' feedback channels must each have one simulation owner.",
                        nameof(components));
                }
            }

            if (component is ConveyorRuntimeConfiguration conveyor)
            {
                if (!actuatorCommandIds.Add(conveyor.RunCommandChannelId)
                    || !actuatorCommandIds.Add(conveyor.ReverseCommandChannelId))
                {
                    throw new ArgumentException(
                        $"Conveyor '{conveyor.Id}' command channels must each control one actuator.",
                        nameof(components));
                }
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
        var controlledDoorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loadLock in loadLocks)
        {
            ArgumentNullException.ThrowIfNull(loadLock);
            if (!loadLocksById.TryAdd(loadLock.Id, loadLock))
            {
                throw new ArgumentException($"Load-lock id '{loadLock.Id}' is duplicated.", nameof(loadLocks));
            }

            ValidateLoadLockDoor(loadLock, loadLock.OuterDoorComponentId, componentsById, controlledDoorIds);
            ValidateLoadLockDoor(loadLock, loadLock.InnerDoorComponentId, componentsById, controlledDoorIds);
            if (!actuatorCommandIds.Add(loadLock.EvacuateCommandChannelId)
                || !actuatorCommandIds.Add(loadLock.VentCommandChannelId))
            {
                throw new ArgumentException(
                    $"Load-lock '{loadLock.Id}' command channels must each control one equipment state.",
                    nameof(loadLocks));
            }

            if (!simulationOwnedInputIds.Add(loadLock.VacuumReadySensorChannelId)
                || !simulationOwnedInputIds.Add(loadLock.AtmosphereReadySensorChannelId))
            {
                throw new ArgumentException(
                    $"Load-lock '{loadLock.Id}' feedback channels must each have one simulation owner.",
                    nameof(loadLocks));
            }
        }

        var waferHandlersById = new SortedDictionary<string, WaferHandlerRuntimeConfiguration>(StringComparer.Ordinal);
        var controlledWorkpieceIds = new HashSet<string>(StringComparer.Ordinal);
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

            if (!controlledWorkpieceIds.Add(handler.WorkpieceComponentId))
            {
                throw new ArgumentException(
                    $"Workpiece '{handler.WorkpieceComponentId}' cannot be controlled by more than one wafer-handler.",
                    nameof(waferHandlers));
            }

            if (!actuatorCommandIds.Add(handler.PickCommandChannelId)
                || !actuatorCommandIds.Add(handler.PlaceCommandChannelId))
            {
                throw new ArgumentException($"Wafer-handler '{handler.Id}' commands must each control one equipment state.", nameof(waferHandlers));
            }

            if (!simulationOwnedInputIds.Add(handler.HoldingFeedbackChannelId)
                || !simulationOwnedInputIds.Add(handler.PlacedFeedbackChannelId))
            {
                throw new ArgumentException($"Wafer-handler '{handler.Id}' feedback channels must each have one simulation owner.", nameof(waferHandlers));
            }
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

            if (!simulationOwnedInputIds.Add(sorter.PassRoutedFeedbackChannelId)
                || !simulationOwnedInputIds.Add(sorter.NgRoutedFeedbackChannelId))
            {
                throw new ArgumentException($"Inspection sorter '{sorter.Id}' feedback channels must each have one simulation owner.", nameof(inspectionSortRouters));
            }
        }

        var inspectionHandoffsById = new SortedDictionary<string, InspectionHandoffRuntimeConfiguration>(StringComparer.Ordinal);
        foreach (var handoff in inspectionHandoffs)
        {
            ArgumentNullException.ThrowIfNull(handoff);
            if (!inspectionHandoffsById.TryAdd(handoff.Id, handoff))
            {
                throw new ArgumentException($"Inspection handoff id '{handoff.Id}' is duplicated.", nameof(inspectionHandoffs));
            }

            if (!actuatorCommandIds.Add(handoff.ResultAcceptedCommandChannelId))
            {
                throw new ArgumentException($"Inspection handoff '{handoff.Id}' result-accepted command must control one equipment state.", nameof(inspectionHandoffs));
            }

            if (!simulationOwnedInputIds.Add(handoff.InspectionReadyFeedbackChannelId)
                || !simulationOwnedInputIds.Add(handoff.InspectionCompleteFeedbackChannelId))
            {
                throw new ArgumentException($"Inspection handoff '{handoff.Id}' feedback channels must each have one simulation owner.", nameof(inspectionHandoffs));
            }
        }

        var ohtHandoffsById = new SortedDictionary<string, OhtHandoffRuntimeConfiguration>(StringComparer.Ordinal);
        var ohtConveyorIds = new HashSet<string>(StringComparer.Ordinal);
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

            if (!ohtConveyorIds.Add(handoff.TransportConveyorComponentId))
            {
                throw new ArgumentException($"Conveyor '{handoff.TransportConveyorComponentId}' cannot be controlled by more than one OHT handoff.", nameof(ohtHandoffs));
            }

            if (!simulationOwnedInputIds.Add(handoff.HandoffReadyFeedbackChannelId)
                || !simulationOwnedInputIds.Add(handoff.CarrierTransferredFeedbackChannelId))
            {
                throw new ArgumentException($"OHT handoff '{handoff.Id}' feedback channels must each have one simulation owner.", nameof(ohtHandoffs));
            }
        }

        var prealignersById = new SortedDictionary<string, PrealignerRuntimeConfiguration>(StringComparer.Ordinal);
        var prealignerStageIds = new HashSet<string>(StringComparer.Ordinal);
        var prealignerClampIds = new HashSet<string>(StringComparer.Ordinal);
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

            if (!prealignerStageIds.Add(prealigner.RotaryStageComponentId)
                || !prealignerClampIds.Add(prealigner.ClampCylinderComponentId))
            {
                throw new ArgumentException($"Pre-aligner '{prealigner.Id}' stage and clamp must each have one semantic owner.", nameof(prealigners));
            }

            if (!actuatorCommandIds.Add(prealigner.AlignmentAcceptedCommandChannelId))
            {
                throw new ArgumentException($"Pre-aligner '{prealigner.Id}' accept command must control one equipment state.", nameof(prealigners));
            }

            if (!simulationOwnedInputIds.Add(prealigner.AlignmentReadyFeedbackChannelId)
                || !simulationOwnedInputIds.Add(prealigner.AlignmentCompleteFeedbackChannelId))
            {
                throw new ArgumentException($"Pre-aligner '{prealigner.Id}' feedback channels must each have one simulation owner.", nameof(prealigners));
            }
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
        ISet<string> controlledDoorIds)
    {
        if (!componentsById.TryGetValue(componentId, out var component)
            || component is not PneumaticCylinderRuntimeConfiguration)
        {
            throw new ArgumentException(
                $"Load-lock '{loadLock.Id}' door '{componentId}' must identify a pneumatic cylinder.");
        }

        if (!controlledDoorIds.Add(componentId))
        {
            throw new ArgumentException(
                $"Pneumatic cylinder '{componentId}' cannot be controlled by more than one load-lock.");
        }
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
