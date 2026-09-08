using System.Collections.ObjectModel;

namespace OpenVisionLab.Machine.Simulation.Layout;

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
