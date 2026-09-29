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
    WaferHandlerOwnershipState? TransferOwnershipState = null,
    string? WorkpieceInstanceId = null,
    bool? IsWorkpiecePresent = null);

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

