using System.Collections.ObjectModel;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.Machine.Simulation.Layout;

/// <summary>
/// Immutable current layout state. Behavior-specific fields are null when they
/// do not apply to the component kind.
/// </summary>
public sealed record LayoutComponentSnapshot(
    string Id,
    string Name,
    LayoutComponentKind Kind,
    double X,
    double Y,
    double RotationDegrees,
    double Width,
    double Height,
    bool? IsDetected,
    int? PendingTransitionTicks,
    PneumaticCylinderState? CylinderState = null,
    double? MotionProgress = null,
    bool? ConveyorRunning = null,
    ConveyorDirection? ConveyorDirection = null,
    double? ConveyorSpeedUnitsPerSecond = null,
    string? CarrierComponentId = null,
    double? CarrierPosition = null,
    string? WorkpieceType = null,
    WorkpieceInspectionState? InspectionState = null,
    string? SensorOutputChannelId = null,
    string? TransferOwnerId = null,
    WaferHandlerOwnershipState? TransferOwnershipState = null);

public enum ConveyorDirection
{
    Forward,
    Reverse
}

public enum PneumaticCylinderState
{
    Retracted,
    Extending,
    Extended,
    Retracting,
    Fault
}

public enum LoadLockState
{
    Atmosphere,
    PumpingDown,
    Vacuum,
    Venting,
    InterlockFault
}

public sealed record LoadLockSnapshot(
    string Id,
    string Name,
    LoadLockState State,
    int RemainingTransitionTicks,
    bool IsVacuumReady,
    bool IsAtmosphereReady,
    bool IsOuterDoorPermitted,
    bool IsInnerDoorPermitted,
    string OuterDoorComponentId,
    string InnerDoorComponentId);

public enum WaferHandlerOwnershipState
{
    Source,
    Handler,
    Destination,
    InterlockFault
}

public sealed record WaferHandlerSnapshot(
    string Id,
    string Name,
    WaferHandlerOwnershipState State,
    string HorizontalAxisId,
    string VerticalAxisId,
    string WorkpieceComponentId,
    double HorizontalPosition,
    double VerticalPosition,
    bool IsSourcePresent,
    bool IsGateOpen,
    bool IsPickPermitted,
    bool IsPlacePermitted);

public enum InspectionSortRouteState
{
    AwaitingDecision,
    PassReady,
    NgReady,
    PassRouted,
    NgRouted,
    InterlockFault
}

public sealed record InspectionSortRouterSnapshot(
    string Id,
    string Name,
    InspectionSortRouteState State,
    string CameraId,
    PlaceholderInspectionDecision? Decision,
    string PassConveyorComponentId,
    string NgConveyorComponentId,
    bool IsPassConveyorRunning,
    bool IsNgConveyorRunning,
    bool IsPassRoutePermitted,
    bool IsNgRoutePermitted);

public enum InspectionHandoffState
{
    AwaitingMaterial,
    Ready,
    Inspecting,
    ResultAvailable,
    Complete,
    InterlockFault
}

public sealed record InspectionHandoffSnapshot(
    string Id,
    string Name,
    InspectionHandoffState State,
    string CameraId,
    PlaceholderInspectionDecision? Decision,
    long AcquisitionOrdinal,
    bool IsMaterialPresent,
    bool IsResultAccepted,
    bool IsInspectionReady,
    bool IsInspectionComplete);

public enum OhtHandoffOwnershipState
{
    Vehicle,
    Ready,
    Transferring,
    LoadPort,
    InterlockFault
}

public sealed record OhtHandoffSnapshot(
    string Id,
    string Name,
    OhtHandoffOwnershipState State,
    string TransportConveyorComponentId,
    bool IsRouteAvailable,
    bool IsVehicleDocked,
    bool IsLoadPortReady,
    bool IsCarrierReceived,
    bool IsForwardCommanded,
    bool IsReverseCommanded,
    bool IsTransferPermitted);

public enum PrealignerState
{
    AwaitingWafer,
    AwaitingClamp,
    Ready,
    Aligning,
    Aligned,
    Released,
    InterlockFault
}

public sealed record PrealignerSnapshot(
    string Id,
    string Name,
    PrealignerState State,
    string RotaryStageComponentId,
    string RotaryAxisId,
    string ClampCylinderComponentId,
    double AlignmentTargetDegrees,
    double AlignmentToleranceDegrees,
    double RotaryPositionDegrees,
    bool IsWaferPresent,
    PneumaticCylinderState ClampState,
    bool IsAlignmentAccepted,
    bool IsAlignmentReady,
    bool IsAlignmentComplete);

public enum MachineLayoutTransitionKind
{
    SensorActivated,
    SensorDeactivated
}

/// <summary>
/// One accepted digital-sensor output transition caused by the layout runtime.
/// </summary>
public sealed record MachineLayoutTransition(
    string ComponentId,
    string OutputChannelId,
    MachineLayoutTransitionKind Kind,
    bool PreviousValue,
    bool CurrentValue,
    long SignalRevision);

public sealed record PneumaticCylinderStateTransition(
    string ComponentId,
    PneumaticCylinderState PreviousState,
    PneumaticCylinderState CurrentState,
    double MotionProgress);

public sealed record PneumaticCylinderFeedbackTransition(
    string ComponentId,
    string ChannelId,
    bool PreviousValue,
    bool CurrentValue,
    long SignalRevision);

public sealed record ConveyorStateTransition(
    string ComponentId,
    bool PreviousRunning,
    bool CurrentRunning,
    ConveyorDirection PreviousDirection,
    ConveyorDirection CurrentDirection,
    double SpeedUnitsPerSecond);

/// <summary>
/// Immutable result of one fixed layout tick.
/// </summary>
public sealed class MachineLayoutTickResult
{
    public MachineLayoutTickResult(
        IEnumerable<LayoutComponentSnapshot> components,
        IEnumerable<MachineLayoutTransition> transitions)
        : this(
            components,
            transitions,
            Array.Empty<PneumaticCylinderStateTransition>(),
            Array.Empty<PneumaticCylinderFeedbackTransition>(),
            Array.Empty<ConveyorStateTransition>(),
            Array.Empty<LoadLockSnapshot>(),
            Array.Empty<WaferHandlerSnapshot>(),
            Array.Empty<InspectionSortRouterSnapshot>(),
            Array.Empty<InspectionHandoffSnapshot>(),
            Array.Empty<OhtHandoffSnapshot>(),
            Array.Empty<PrealignerSnapshot>())
    {
    }

    public MachineLayoutTickResult(
        IEnumerable<LayoutComponentSnapshot> components,
        IEnumerable<MachineLayoutTransition> transitions,
        IEnumerable<PneumaticCylinderStateTransition> cylinderStateTransitions,
        IEnumerable<PneumaticCylinderFeedbackTransition> cylinderFeedbackTransitions,
        IEnumerable<ConveyorStateTransition>? conveyorStateTransitions = null,
        IEnumerable<LoadLockSnapshot>? loadLocks = null,
        IEnumerable<WaferHandlerSnapshot>? waferHandlers = null,
        IEnumerable<InspectionSortRouterSnapshot>? inspectionSortRouters = null,
        IEnumerable<InspectionHandoffSnapshot>? inspectionHandoffs = null,
        IEnumerable<OhtHandoffSnapshot>? ohtHandoffs = null,
        IEnumerable<PrealignerSnapshot>? prealigners = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(transitions);
        ArgumentNullException.ThrowIfNull(cylinderStateTransitions);
        ArgumentNullException.ThrowIfNull(cylinderFeedbackTransitions);
        conveyorStateTransitions ??= Array.Empty<ConveyorStateTransition>();
        loadLocks ??= Array.Empty<LoadLockSnapshot>();
        waferHandlers ??= Array.Empty<WaferHandlerSnapshot>();
        inspectionSortRouters ??= Array.Empty<InspectionSortRouterSnapshot>();
        inspectionHandoffs ??= Array.Empty<InspectionHandoffSnapshot>();
        ohtHandoffs ??= Array.Empty<OhtHandoffSnapshot>();
        prealigners ??= Array.Empty<PrealignerSnapshot>();

        Components = new ReadOnlyCollection<LayoutComponentSnapshot>(components.ToArray());
        Transitions = new ReadOnlyCollection<MachineLayoutTransition>(transitions.ToArray());
        CylinderStateTransitions = new ReadOnlyCollection<PneumaticCylinderStateTransition>(
            cylinderStateTransitions.ToArray());
        CylinderFeedbackTransitions = new ReadOnlyCollection<PneumaticCylinderFeedbackTransition>(
            cylinderFeedbackTransitions.ToArray());
        ConveyorStateTransitions = new ReadOnlyCollection<ConveyorStateTransition>(
            conveyorStateTransitions.ToArray());
        LoadLocks = new ReadOnlyCollection<LoadLockSnapshot>(loadLocks.ToArray());
        WaferHandlers = new ReadOnlyCollection<WaferHandlerSnapshot>(waferHandlers.ToArray());
        InspectionSortRouters = new ReadOnlyCollection<InspectionSortRouterSnapshot>(inspectionSortRouters.ToArray());
        InspectionHandoffs = new ReadOnlyCollection<InspectionHandoffSnapshot>(inspectionHandoffs.ToArray());
        OhtHandoffs = new ReadOnlyCollection<OhtHandoffSnapshot>(ohtHandoffs.ToArray());
        Prealigners = new ReadOnlyCollection<PrealignerSnapshot>(prealigners.ToArray());
    }

    public ReadOnlyCollection<LayoutComponentSnapshot> Components { get; }
    public ReadOnlyCollection<MachineLayoutTransition> Transitions { get; }
    public ReadOnlyCollection<PneumaticCylinderStateTransition> CylinderStateTransitions { get; }
    public ReadOnlyCollection<PneumaticCylinderFeedbackTransition> CylinderFeedbackTransitions { get; }
    public ReadOnlyCollection<ConveyorStateTransition> ConveyorStateTransitions { get; }
    public ReadOnlyCollection<LoadLockSnapshot> LoadLocks { get; }
    public ReadOnlyCollection<WaferHandlerSnapshot> WaferHandlers { get; }
    public ReadOnlyCollection<InspectionSortRouterSnapshot> InspectionSortRouters { get; }
    public ReadOnlyCollection<InspectionHandoffSnapshot> InspectionHandoffs { get; }
    public ReadOnlyCollection<OhtHandoffSnapshot> OhtHandoffs { get; }
    public ReadOnlyCollection<PrealignerSnapshot> Prealigners { get; }
}
