using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum RecipeConnectionProjectApplyOutcome
{
    Applied,
    NoChanges,
    MultipleDevices
}

internal sealed record RecipeConnectionProjectApplyResult(
    RecipeConnectionProjectApplyOutcome Outcome,
    int ChangeCount = 0,
    int AppliedCount = 0,
    string? EntityId = null,
    int AddedConnectionCount = 0,
    int AddedStepCount = 0,
    int RemovedStepCount = 0,
    int AppliedStepCount = 0)
{
    internal bool Changed => Outcome == RecipeConnectionProjectApplyOutcome.Applied;
}

/// <summary>
/// Applies typed Recipe Connection setup drafts to an authored project.
/// Presentation, dirty-state, runtime, and WPF follow-up remain in the shell.
/// </summary>
internal sealed class RecipeConnectionProjectApplier
{
    private readonly SemiconductorStationSkeletonTemplate _stationSkeletonTemplate = new();
    private readonly SemiconductorProcessBlockComposer _processBlockComposer = new();

    internal RecipeConnectionProjectApplyResult ApplyStationSkeleton(
        MachineProjectDocument project,
        SemiconductorStationSetupDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var result = _stationSkeletonTemplate.Apply(project, setup);
        return result.Changed
            ? Applied(
                Math.Max(1, result.AppliedCount),
                appliedCount: result.AppliedCount)
            : NoChanges();
    }

    internal RecipeConnectionProjectApplyResult ApplyLoadLockSetup(
        MachineProjectDocument project,
        LoadLockDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var (device, hasMultiple) = FindSingleDevice(project, DeviceKind.LoadLock);
        if (hasMultiple)
        {
            return MultipleDevices();
        }

        var channelIds = new[]
        {
            setup.EvacuateCommandChannelId,
            setup.VentCommandChannelId,
            setup.VacuumReadySensorChannelId,
            setup.AtmosphereReadySensorChannelId
        };
        var current = device?.LoadLock;
        var changed = current is null
            || !string.Equals(current.OuterDoorComponentId, setup.OuterDoorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.InnerDoorComponentId, setup.InnerDoorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.EvacuateCommandChannelId, setup.EvacuateCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.VentCommandChannelId, setup.VentCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.VacuumReadySensorChannelId, setup.VacuumReadySensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.AtmosphereReadySensorChannelId, setup.AtmosphereReadySensorChannelId, StringComparison.Ordinal)
            || current.PumpDownDurationMilliseconds != setup.PumpDownDurationMilliseconds
            || current.VentDurationMilliseconds != setup.VentDurationMilliseconds
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            return NoChanges();
        }

        device ??= CreateDevice(
            project,
            "load-lock",
            "Load Lock Chamber",
            DeviceKind.LoadLock);
        device.ChannelIds = [.. channelIds];
        device.LoadLock = new LoadLockDefinition
        {
            OuterDoorComponentId = setup.OuterDoorComponentId,
            InnerDoorComponentId = setup.InnerDoorComponentId,
            EvacuateCommandChannelId = setup.EvacuateCommandChannelId,
            VentCommandChannelId = setup.VentCommandChannelId,
            VacuumReadySensorChannelId = setup.VacuumReadySensorChannelId,
            AtmosphereReadySensorChannelId = setup.AtmosphereReadySensorChannelId,
            PumpDownDurationMilliseconds = setup.PumpDownDurationMilliseconds,
            VentDurationMilliseconds = setup.VentDurationMilliseconds
        };
        return Applied(1, entityId: device.Id);
    }

    internal RecipeConnectionProjectApplyResult ApplyWaferHandlerSetup(
        MachineProjectDocument project,
        WaferHandlerDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var (device, hasMultiple) = FindSingleDevice(project, DeviceKind.Handler);
        if (hasMultiple)
        {
            return MultipleDevices();
        }

        var channelIds = new[]
        {
            setup.SourcePresentSensorChannelId,
            setup.GateOpenSensorChannelId,
            setup.PickCommandChannelId,
            setup.PlaceCommandChannelId,
            setup.HoldingFeedbackChannelId,
            setup.PlacedFeedbackChannelId
        };
        var current = device?.WaferHandler;
        var changed = current is null
            || !string.Equals(current.HorizontalAxisId, setup.HorizontalAxisId, StringComparison.Ordinal)
            || !string.Equals(current.VerticalAxisId, setup.VerticalAxisId, StringComparison.Ordinal)
            || !string.Equals(current.WorkpieceComponentId, setup.WorkpieceComponentId, StringComparison.Ordinal)
            || !string.Equals(current.SourcePresentSensorChannelId, setup.SourcePresentSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.GateOpenSensorChannelId, setup.GateOpenSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.PickCommandChannelId, setup.PickCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.PlaceCommandChannelId, setup.PlaceCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.HoldingFeedbackChannelId, setup.HoldingFeedbackChannelId, StringComparison.Ordinal)
            || !string.Equals(current.PlacedFeedbackChannelId, setup.PlacedFeedbackChannelId, StringComparison.Ordinal)
            || current.PickHorizontalPosition != setup.PickHorizontalPosition
            || current.PickVerticalPosition != setup.PickVerticalPosition
            || current.PlaceHorizontalPosition != setup.PlaceHorizontalPosition
            || current.PlaceVerticalPosition != setup.PlaceVerticalPosition
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            return NoChanges();
        }

        device ??= CreateDevice(
            project,
            "wafer-handler",
            "Wafer Handler",
            DeviceKind.Handler);
        device.ChannelIds = [.. channelIds];
        device.WaferHandler = new WaferHandlerDefinition
        {
            HorizontalAxisId = setup.HorizontalAxisId,
            VerticalAxisId = setup.VerticalAxisId,
            WorkpieceComponentId = setup.WorkpieceComponentId,
            SourcePresentSensorChannelId = setup.SourcePresentSensorChannelId,
            GateOpenSensorChannelId = setup.GateOpenSensorChannelId,
            PickCommandChannelId = setup.PickCommandChannelId,
            PlaceCommandChannelId = setup.PlaceCommandChannelId,
            HoldingFeedbackChannelId = setup.HoldingFeedbackChannelId,
            PlacedFeedbackChannelId = setup.PlacedFeedbackChannelId,
            PickHorizontalPosition = setup.PickHorizontalPosition,
            PickVerticalPosition = setup.PickVerticalPosition,
            PlaceHorizontalPosition = setup.PlaceHorizontalPosition,
            PlaceVerticalPosition = setup.PlaceVerticalPosition
        };
        return Applied(1, entityId: device.Id);
    }

    internal RecipeConnectionProjectApplyResult ApplyPrealignerSetup(
        MachineProjectDocument project,
        PrealignerDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var (device, hasMultiple) = FindSingleDevice(project, DeviceKind.Prealigner);
        if (hasMultiple)
        {
            return MultipleDevices();
        }

        var channelIds = new[]
        {
            setup.WaferPresentSensorChannelId,
            setup.AlignmentAcceptedCommandChannelId,
            setup.AlignmentReadyFeedbackChannelId,
            setup.AlignmentCompleteFeedbackChannelId
        };
        var current = device?.Prealigner;
        var changed = current is null
            || !string.Equals(current.RotaryStageComponentId, setup.RotaryStageComponentId, StringComparison.Ordinal)
            || !string.Equals(current.ClampCylinderComponentId, setup.ClampCylinderComponentId, StringComparison.Ordinal)
            || !string.Equals(current.WaferPresentSensorChannelId, setup.WaferPresentSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.AlignmentAcceptedCommandChannelId, setup.AlignmentAcceptedCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.AlignmentReadyFeedbackChannelId, setup.AlignmentReadyFeedbackChannelId, StringComparison.Ordinal)
            || !string.Equals(current.AlignmentCompleteFeedbackChannelId, setup.AlignmentCompleteFeedbackChannelId, StringComparison.Ordinal)
            || current.AlignmentTargetDegrees != setup.AlignmentTargetDegrees
            || current.AlignmentToleranceDegrees != setup.AlignmentToleranceDegrees
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            return NoChanges();
        }

        device ??= CreateDevice(
            project,
            "prealigner",
            "Pre-aligner",
            DeviceKind.Prealigner);
        device.ChannelIds = [.. channelIds];
        device.Prealigner = new PrealignerDefinition
        {
            RotaryStageComponentId = setup.RotaryStageComponentId,
            ClampCylinderComponentId = setup.ClampCylinderComponentId,
            WaferPresentSensorChannelId = setup.WaferPresentSensorChannelId,
            AlignmentAcceptedCommandChannelId = setup.AlignmentAcceptedCommandChannelId,
            AlignmentReadyFeedbackChannelId = setup.AlignmentReadyFeedbackChannelId,
            AlignmentCompleteFeedbackChannelId = setup.AlignmentCompleteFeedbackChannelId,
            AlignmentTargetDegrees = setup.AlignmentTargetDegrees,
            AlignmentToleranceDegrees = setup.AlignmentToleranceDegrees
        };
        return Applied(1, entityId: device.Id);
    }

    internal RecipeConnectionProjectApplyResult ApplyInspectionHandoffSetup(
        MachineProjectDocument project,
        InspectionHandoffDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var (device, hasMultiple) = FindSingleDevice(project, DeviceKind.Inspection);
        if (hasMultiple)
        {
            return MultipleDevices();
        }

        var channelIds = new[]
        {
            setup.InspectionPositionSensorChannelId,
            setup.ResultAcceptedCommandChannelId,
            setup.InspectionReadyFeedbackChannelId,
            setup.InspectionCompleteFeedbackChannelId
        };
        var current = device?.InspectionHandoff;
        var changed = current is null
            || !string.Equals(current.CameraId, setup.CameraId, StringComparison.Ordinal)
            || !string.Equals(current.InspectionPositionSensorChannelId, setup.InspectionPositionSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.ResultAcceptedCommandChannelId, setup.ResultAcceptedCommandChannelId, StringComparison.Ordinal)
            || !string.Equals(current.InspectionReadyFeedbackChannelId, setup.InspectionReadyFeedbackChannelId, StringComparison.Ordinal)
            || !string.Equals(current.InspectionCompleteFeedbackChannelId, setup.InspectionCompleteFeedbackChannelId, StringComparison.Ordinal)
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            return NoChanges();
        }

        device ??= CreateDevice(
            project,
            "inspection-handoff",
            "Inspection Handoff",
            DeviceKind.Inspection);
        device.ChannelIds = [.. channelIds];
        device.InspectionHandoff = new InspectionHandoffDefinition
        {
            CameraId = setup.CameraId,
            InspectionPositionSensorChannelId = setup.InspectionPositionSensorChannelId,
            ResultAcceptedCommandChannelId = setup.ResultAcceptedCommandChannelId,
            InspectionReadyFeedbackChannelId = setup.InspectionReadyFeedbackChannelId,
            InspectionCompleteFeedbackChannelId = setup.InspectionCompleteFeedbackChannelId
        };
        return Applied(1, entityId: device.Id);
    }

    internal RecipeConnectionProjectApplyResult ApplyInspectionSortRouterSetup(
        MachineProjectDocument project,
        InspectionSortRouterDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var (device, hasMultiple) = FindSingleDevice(project, DeviceKind.Sorter);
        if (hasMultiple)
        {
            return MultipleDevices();
        }

        var channelIds = new[]
        {
            setup.PassRoutedFeedbackChannelId,
            setup.NgRoutedFeedbackChannelId
        };
        var current = device?.InspectionSortRouter;
        var changed = current is null
            || !string.Equals(current.CameraId, setup.CameraId, StringComparison.Ordinal)
            || !string.Equals(current.PassConveyorComponentId, setup.PassConveyorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.NgConveyorComponentId, setup.NgConveyorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.PassRoutedFeedbackChannelId, setup.PassRoutedFeedbackChannelId, StringComparison.Ordinal)
            || !string.Equals(current.NgRoutedFeedbackChannelId, setup.NgRoutedFeedbackChannelId, StringComparison.Ordinal)
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            return NoChanges();
        }

        device ??= CreateDevice(
            project,
            "inspection-sorter",
            "Inspection Sorter",
            DeviceKind.Sorter);
        device.ChannelIds = [.. channelIds];
        device.InspectionSortRouter = new InspectionSortRouterDefinition
        {
            CameraId = setup.CameraId,
            PassConveyorComponentId = setup.PassConveyorComponentId,
            NgConveyorComponentId = setup.NgConveyorComponentId,
            PassRoutedFeedbackChannelId = setup.PassRoutedFeedbackChannelId,
            NgRoutedFeedbackChannelId = setup.NgRoutedFeedbackChannelId
        };
        return Applied(1, entityId: device.Id);
    }

    internal RecipeConnectionProjectApplyResult ApplyOhtHandoffSetup(
        MachineProjectDocument project,
        OhtHandoffDefinition setup)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(setup);

        var (device, hasMultiple) = FindSingleDevice(project, DeviceKind.Oht);
        if (hasMultiple)
        {
            return MultipleDevices();
        }

        var channelIds = new[]
        {
            setup.RouteAvailableSensorChannelId,
            setup.VehicleDockedSensorChannelId,
            setup.LoadPortReadySensorChannelId,
            setup.CarrierReceivedSensorChannelId,
            setup.HandoffReadyFeedbackChannelId,
            setup.CarrierTransferredFeedbackChannelId
        };
        var current = device?.OhtHandoff;
        var changed = current is null
            || !string.Equals(current.TransportConveyorComponentId, setup.TransportConveyorComponentId, StringComparison.Ordinal)
            || !string.Equals(current.RouteAvailableSensorChannelId, setup.RouteAvailableSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.VehicleDockedSensorChannelId, setup.VehicleDockedSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.LoadPortReadySensorChannelId, setup.LoadPortReadySensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.CarrierReceivedSensorChannelId, setup.CarrierReceivedSensorChannelId, StringComparison.Ordinal)
            || !string.Equals(current.HandoffReadyFeedbackChannelId, setup.HandoffReadyFeedbackChannelId, StringComparison.Ordinal)
            || !string.Equals(current.CarrierTransferredFeedbackChannelId, setup.CarrierTransferredFeedbackChannelId, StringComparison.Ordinal)
            || device is null
            || !device.ChannelIds.SequenceEqual(channelIds, StringComparer.Ordinal);
        if (!changed)
        {
            return NoChanges();
        }

        device ??= CreateDevice(
            project,
            "oht-handoff",
            "OHT Handoff",
            DeviceKind.Oht);
        device.ChannelIds = [.. channelIds];
        device.OhtHandoff = new OhtHandoffDefinition
        {
            TransportConveyorComponentId = setup.TransportConveyorComponentId,
            RouteAvailableSensorChannelId = setup.RouteAvailableSensorChannelId,
            VehicleDockedSensorChannelId = setup.VehicleDockedSensorChannelId,
            LoadPortReadySensorChannelId = setup.LoadPortReadySensorChannelId,
            CarrierReceivedSensorChannelId = setup.CarrierReceivedSensorChannelId,
            HandoffReadyFeedbackChannelId = setup.HandoffReadyFeedbackChannelId,
            CarrierTransferredFeedbackChannelId = setup.CarrierTransferredFeedbackChannelId
        };
        return Applied(1, entityId: device.Id);
    }

    internal RecipeConnectionProjectApplyResult ApplyProcessBlocks(
        MachineProjectDocument project,
        IReadOnlyList<SemiconductorProcessBlockKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(kinds);

        var result = _processBlockComposer.Apply(project, kinds);
        return result.Changed
            ? Applied(
                result.AddedConnectionCount + result.AddedStepCount + result.RemovedStepCount,
                addedConnectionCount: result.AddedConnectionCount,
                addedStepCount: result.AddedStepCount,
                removedStepCount: result.RemovedStepCount)
            : NoChanges();
    }

    internal RecipeConnectionProjectApplyResult ApplyProcessBlockTimeouts(
        MachineProjectDocument project,
        SemiconductorManagedTimeoutAdjustmentPreview preview)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(preview);

        var result = _processBlockComposer.ApplyTimeoutAdjustment(project, preview);
        return result.Changed
            ? Applied(result.AppliedStepCount, appliedStepCount: result.AppliedStepCount)
            : NoChanges();
    }

    private static (DeviceDefinition? Device, bool HasMultiple) FindSingleDevice(
        MachineProjectDocument project,
        DeviceKind kind)
    {
        var devices = project.Devices.Where(device => device.Kind == kind).ToArray();
        return devices.Length switch
        {
            0 => (null, false),
            1 => (devices[0], false),
            _ => (null, true)
        };
    }

    private static DeviceDefinition CreateDevice(
        MachineProjectDocument project,
        string prefix,
        string name,
        DeviceKind kind)
    {
        var ordinal = NextOrdinal(prefix, project.Devices.Select(device => device.Id));
        var device = new DeviceDefinition
        {
            Id = $"{prefix}-{ordinal}",
            Name = $"{name} {ordinal}",
            Kind = kind,
            MountPosition = new Coordinate3D(0, 0, 0)
        };
        project.Devices.Add(device);
        return device;
    }

    private static RecipeConnectionProjectApplyResult Applied(
        int changeCount,
        int appliedCount = 0,
        string? entityId = null,
        int addedConnectionCount = 0,
        int addedStepCount = 0,
        int removedStepCount = 0,
        int appliedStepCount = 0) =>
        new(
            RecipeConnectionProjectApplyOutcome.Applied,
            changeCount,
            appliedCount,
            entityId,
            addedConnectionCount,
            addedStepCount,
            removedStepCount,
            appliedStepCount);

    private static RecipeConnectionProjectApplyResult NoChanges() =>
        new(RecipeConnectionProjectApplyOutcome.NoChanges);

    private static RecipeConnectionProjectApplyResult MultipleDevices() =>
        new(RecipeConnectionProjectApplyOutcome.MultipleDevices);

    private static int NextOrdinal(string prefix, IEnumerable<string> ids)
    {
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        var ordinal = 1;
        while (existing.Contains($"{prefix}-{ordinal}"))
        {
            ordinal++;
        }

        return ordinal;
    }
}
