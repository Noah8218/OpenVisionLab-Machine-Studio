namespace OpenVisionLab.Machine.Simulation.Layout;

/// <summary>
/// Owns runtime-state reset and workpiece carrier-position initialization for a
/// deterministic machine layout.
/// </summary>
internal sealed class MachineLayoutRuntimeResetter
{
    private readonly IReadOnlyDictionary<string, LayoutComponentRuntimeState> _componentsById;
    private readonly IReadOnlyList<LayoutComponentRuntimeState> _orderedComponents;
    private readonly IReadOnlyList<WorkpieceRuntimeState> _orderedWorkpieces;
    private readonly IReadOnlyList<LoadLockRuntimeState> _orderedLoadLocks;
    private readonly IReadOnlyList<WaferHandlerRuntimeState> _orderedWaferHandlers;
    private readonly IReadOnlyList<InspectionSortRouterRuntimeState> _orderedInspectionSortRouters;
    private readonly IReadOnlyList<InspectionHandoffRuntimeState> _orderedInspectionHandoffs;
    private readonly IReadOnlyList<OhtHandoffRuntimeState> _orderedOhtHandoffs;
    private readonly IReadOnlyList<PrealignerRuntimeState> _orderedPrealigners;

    public MachineLayoutRuntimeResetter(
        IReadOnlyDictionary<string, LayoutComponentRuntimeState> componentsById,
        IReadOnlyList<LayoutComponentRuntimeState> orderedComponents,
        IReadOnlyList<WorkpieceRuntimeState> orderedWorkpieces,
        IReadOnlyList<LoadLockRuntimeState> orderedLoadLocks,
        IReadOnlyList<WaferHandlerRuntimeState> orderedWaferHandlers,
        IReadOnlyList<InspectionSortRouterRuntimeState> orderedInspectionSortRouters,
        IReadOnlyList<InspectionHandoffRuntimeState> orderedInspectionHandoffs,
        IReadOnlyList<OhtHandoffRuntimeState> orderedOhtHandoffs,
        IReadOnlyList<PrealignerRuntimeState> orderedPrealigners)
    {
        _componentsById = componentsById ?? throw new ArgumentNullException(nameof(componentsById));
        _orderedComponents = orderedComponents ?? throw new ArgumentNullException(nameof(orderedComponents));
        _orderedWorkpieces = orderedWorkpieces ?? throw new ArgumentNullException(nameof(orderedWorkpieces));
        _orderedLoadLocks = orderedLoadLocks ?? throw new ArgumentNullException(nameof(orderedLoadLocks));
        _orderedWaferHandlers = orderedWaferHandlers ?? throw new ArgumentNullException(nameof(orderedWaferHandlers));
        _orderedInspectionSortRouters = orderedInspectionSortRouters ?? throw new ArgumentNullException(nameof(orderedInspectionSortRouters));
        _orderedInspectionHandoffs = orderedInspectionHandoffs ?? throw new ArgumentNullException(nameof(orderedInspectionHandoffs));
        _orderedOhtHandoffs = orderedOhtHandoffs ?? throw new ArgumentNullException(nameof(orderedOhtHandoffs));
        _orderedPrealigners = orderedPrealigners ?? throw new ArgumentNullException(nameof(orderedPrealigners));
    }

    public void InitializeWorkpieceCarrierPositions()
    {
        foreach (var workpiece in _orderedWorkpieces)
        {
            var conveyor = (ConveyorRuntimeState)_componentsById[
                workpiece.WorkpieceConfiguration.ConveyorComponentId];
            workpiece.UpdateCarrierPosition(conveyor);
        }
    }

    /// <summary>
    /// Resets runtime states and invokes signal restoration between component
    /// pose reset and specialized device-state reset.
    /// </summary>
    public void Reset(Action resetSimulationOwnedSignals)
    {
        ArgumentNullException.ThrowIfNull(resetSimulationOwnedSignals);

        foreach (var component in _orderedComponents)
        {
            component.Reset();
        }
        InitializeWorkpieceCarrierPositions();
        resetSimulationOwnedSignals();

        foreach (var loadLock in _orderedLoadLocks)
        {
            loadLock.Reset();
        }

        foreach (var handler in _orderedWaferHandlers)
        {
            handler.Reset();
        }

        foreach (var sorter in _orderedInspectionSortRouters)
        {
            sorter.Reset();
        }

        foreach (var handoff in _orderedInspectionHandoffs)
        {
            handoff.Reset();
        }

        foreach (var handoff in _orderedOhtHandoffs)
        {
            handoff.Reset();
        }

        foreach (var prealigner in _orderedPrealigners)
        {
            prealigner.Reset();
        }
    }
}
