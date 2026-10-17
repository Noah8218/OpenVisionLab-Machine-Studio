using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;

namespace OpenVisionLab.Machine.Simulation.Workpieces;

public sealed record UnitMaterialFlowConfiguration(bool Enabled, string WorkpieceName, string OkRoute, string NgRoute,
    IReadOnlyList<string> UnitIds, IReadOnlyDictionary<string, IReadOnlyList<string>> InspectionCameraIds,
    IReadOnlyDictionary<string, string> ConveyorUnitIds, int? TotalFeedCount = null,
    IReadOnlyList<UnitMaterialPositionConfiguration>? Positions = null);

public sealed record UnitMaterialPositionConfiguration(string UnitId, bool IsExit, string ConveyorId, string? Route, string Name);
public sealed record UnitMaterialSupplySnapshot(int? TargetCount, int FedCount, int ExitedCount, bool HasInFlight);

public sealed record UnitMaterialSlotSnapshot(string UnitId, string? InstanceId, string State, string? DestinationUnitId,
    double TransferProgress, string? Verdict, int InspectionCount, int RequiredInspectionCount, string WorkpieceName = "");

public sealed record UnitMaterialExitSnapshot(string InstanceId, string UnitId, string Verdict, string Route);

/// <summary>Owns capacity-one unit slots, reservations and instance-correlated results on the simulation thread.</summary>
public sealed class UnitMaterialFlowRuntime
{
    private readonly UnitMaterialFlowConfiguration _configuration;
    private readonly Dictionary<string, Slot> _slots;
    private readonly Dictionary<string, Dictionary<string, bool>> _unitResults;
    private readonly List<UnitMaterialExitSnapshot> _exits = new();
    private readonly string[] _requiredCameras;
    private int _ordinal;
    private string _runId = "unstarted";

    public UnitMaterialFlowRuntime(UnitMaterialFlowConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (configuration.TotalFeedCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(configuration), "Total supply must be from 1 to 100.");
        _slots = configuration.UnitIds.ToDictionary(id => id, _ => new Slot(), StringComparer.Ordinal);
        _unitResults = configuration.UnitIds.ToDictionary(id => id, _ => new Dictionary<string, bool>(StringComparer.Ordinal), StringComparer.Ordinal);
        _requiredCameras = configuration.InspectionCameraIds.Values.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).ToArray();
    }

    public bool Enabled => _configuration.Enabled;
    public bool HasInFlight => _slots.Values.Any(slot => slot.Piece is not null || slot.ReservedBy is not null || slot.Motion is not null);
    public UnitMaterialSupplySnapshot Supply => new(_configuration.TotalFeedCount, _ordinal, _exits.Count, HasInFlight);
    private bool SupplyExhausted => Enabled && _configuration.TotalFeedCount is { } count && _ordinal >= count;
    public IReadOnlyList<UnitMaterialExitSnapshot> Exits => _exits.ToArray();
    public string? InstanceId(string unitId) => _slots.GetValueOrDefault(unitId)?.Piece?.Id;

    public void Reset(string runId)
    {
        _runId = runId;
        _ordinal = 0;
        _exits.Clear();
        foreach (var slot in _slots.Values) { slot.Piece = null; slot.ReservedBy = null; slot.Motion = null; }
        foreach (var results in _unitResults.Values) results.Clear();
    }

    public void CancelTransfers()
    {
        foreach (var slot in _slots.Values) { slot.ReservedBy = null; slot.Motion = null; }
    }

    public SequenceUnitOperationResult Execute(UnitOperationStep step, TimeSpan elapsed)
    {
        if (!_slots.TryGetValue(step.UnitId, out var slot)) return SequenceUnitOperationResult.Failure("Owning unit does not exist.");
        if (!Enabled && step.Action != SequenceStepAction.EjectUnitWorkpiece)
            return SequenceUnitOperationResult.Failure("Enable material flow before feeding, receiving or transferring.");
        switch (step.Action)
        {
            case SequenceStepAction.FeedUnitWorkpiece:
                if (slot.Piece is not null || slot.ReservedBy is not null) return SequenceUnitOperationResult.Waiting();
                if (SupplyExhausted) return SequenceUnitOperationResult.SupplyFinished();
                slot.Piece = new Piece($"{_runId}/WP{++_ordinal:000}");
                return SequenceUnitOperationResult.Complete();
            case SequenceStepAction.ReceiveUnitWorkpiece:
                if (SupplyExhausted && !HasInFlight) return SequenceUnitOperationResult.SupplyFinished();
                return slot.Piece is not null && slot.Motion is null ? SequenceUnitOperationResult.Complete() : SequenceUnitOperationResult.Waiting();
            case SequenceStepAction.TransferUnitWorkpiece:
                return Transfer(step, slot, elapsed);
            case SequenceStepAction.EjectUnitWorkpiece:
                return Eject(step, slot, elapsed);
            default:
                return SequenceUnitOperationResult.Failure("Unsupported material operation.");
        }
    }

    public bool BeginCapture(string unitId, string cameraId, out string? instanceId)
    {
        instanceId = InstanceId(unitId);
        if (!_unitResults.TryGetValue(unitId, out var results) || Enabled && instanceId is null) return false;
        results.Remove(cameraId);
        _slots[unitId].Piece?.Results.Remove(cameraId);
        return true;
    }

    public void RecordInspection(string unitId, string cameraId, string? instanceId, SequenceVisionResultState state)
    {
        if (state is not (SequenceVisionResultState.Passed or SequenceVisionResultState.Failed)) return;
        if (!_unitResults.TryGetValue(unitId, out var results)) return;
        var piece = _slots[unitId].Piece;
        if (Enabled && (piece is null || piece.Id != instanceId)) return;
        bool passed = state == SequenceVisionResultState.Passed;
        results[cameraId] = passed;
        if (piece is not null) piece.Results[cameraId] = passed;
    }

    public SequenceVisionResultReadResult ReadInspection(string unitId)
    {
        if (!_slots.ContainsKey(unitId)) return SequenceVisionResultReadResult.Failure(SequenceContextErrorCode.TargetNotFound, "Unit does not exist.");
        var results = Enabled ? _slots[unitId].Piece?.Results : _unitResults[unitId];
        var required = _configuration.InspectionCameraIds.GetValueOrDefault(unitId) ?? [];
        if (required.Count == 0) required = _requiredCameras;
        if (results is null || required.Count == 0 || required.Any(id => !results.ContainsKey(id)))
            return SequenceVisionResultReadResult.Success(SequenceVisionResultState.NotTriggered);
        return SequenceVisionResultReadResult.Success(required.Any(id => !results[id]) ? SequenceVisionResultState.Failed : SequenceVisionResultState.Passed);
    }

    public IReadOnlyList<UnitMaterialSlotSnapshot> CaptureSlots() => _slots.Select(pair =>
    {
        var slot = pair.Value;
        var piece = slot.Piece;
        string? verdict = piece is null || _requiredCameras.Length == 0 || _requiredCameras.Any(id => !piece.Results.ContainsKey(id))
            ? null : _requiredCameras.Any(id => !piece.Results[id]) ? "NG" : "OK";
        string state = slot.Motion is not null ? "Moving" : piece is not null ? "Occupied" : slot.ReservedBy is not null ? "Reserved" : "Empty";
        return new UnitMaterialSlotSnapshot(pair.Key, piece?.Id, state, slot.Motion?.DestinationUnitId,
            slot.Motion?.Progress ?? 0, verdict, piece?.Results.Count ?? 0, _requiredCameras.Length, _configuration.WorkpieceName);
    }).ToArray();

    private SequenceUnitOperationResult Transfer(UnitOperationStep step, Slot source, TimeSpan elapsed)
    {
        if (step.DestinationUnitId is null || step.DestinationUnitId == step.UnitId || !_slots.TryGetValue(step.DestinationUnitId, out var destination))
            return SequenceUnitOperationResult.Failure("Transfer destination is missing or identical to its source.");
        if (step.ConveyorId is null || _configuration.ConveyorUnitIds.GetValueOrDefault(step.ConveyorId) != step.UnitId)
            return SequenceUnitOperationResult.Failure("Transfer conveyor must belong to the source unit.");
        if (source.Piece is null) return SequenceUnitOperationResult.Waiting();
        if (source.Motion is null)
        {
            if (destination.Piece is not null || destination.ReservedBy is not null) return SequenceUnitOperationResult.Waiting();
            destination.ReservedBy = step.UnitId;
            source.Motion = new Motion(step.Id, step.DestinationUnitId, elapsed);
        }
        if (source.Motion.StepId != step.Id || source.Motion.DestinationUnitId != step.DestinationUnitId)
            return SequenceUnitOperationResult.Failure("Another transfer already owns the source slot.");
        source.Motion.Progress = step.Duration <= TimeSpan.Zero ? 1 : Math.Clamp((elapsed - source.Motion.StartElapsed).TotalSeconds / step.Duration.TotalSeconds, 0, 1);
        if (source.Motion.Progress < 1) return SequenceUnitOperationResult.Waiting();
        if (destination.Piece is not null || destination.ReservedBy != step.UnitId)
            return SequenceUnitOperationResult.Failure("Destination reservation changed during transfer.");
        destination.Piece = source.Piece;
        destination.ReservedBy = null;
        source.Piece = null;
        source.Motion = null;
        return SequenceUnitOperationResult.Complete();
    }

    private SequenceUnitOperationResult Eject(UnitOperationStep step, Slot slot, TimeSpan elapsed)
    {
        var results = Enabled ? slot.Piece?.Results : _unitResults[step.UnitId];
        if (Enabled && slot.Piece is null) return SequenceUnitOperationResult.Waiting();
        var required = Enabled ? _requiredCameras : (_configuration.InspectionCameraIds.GetValueOrDefault(step.UnitId) ?? []).ToArray();
        if (results is null || required.Any(id => !results.ContainsKey(id)))
            return SequenceUnitOperationResult.Failure("Every required inspection must finish before ejection.");
        string verdict = required.Any(id => !results[id]) ? "NG" : "OK";
        if (step.Route != "auto" && step.Route != verdict)
            return SequenceUnitOperationResult.Failure("Selected ejection route disagrees with the instance verdict.");
        if (elapsed < step.Duration) return SequenceUnitOperationResult.Waiting();
        var exit = _configuration.Positions?.FirstOrDefault(position => position.IsExit && position.UnitId == step.UnitId && position.Route == verdict)
            ?? _configuration.Positions?.FirstOrDefault(position => position.IsExit && position.UnitId == step.UnitId && position.Route == "auto");
        _exits.Add(new UnitMaterialExitSnapshot(slot.Piece?.Id ?? $"{_runId}/cycle{++_ordinal:000}", step.UnitId,
            verdict, exit?.Name ?? (verdict == "NG" ? _configuration.NgRoute : _configuration.OkRoute)));
        slot.Piece = null;
        return SequenceUnitOperationResult.Complete();
    }

    private sealed class Slot
    {
        public Piece? Piece;
        public string? ReservedBy;
        public Motion? Motion;
    }

    private sealed class Piece(string id)
    {
        public string Id { get; } = id;
        public Dictionary<string, bool> Results { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Motion(string stepId, string destinationUnitId, TimeSpan startElapsed)
    {
        public string StepId { get; } = stepId;
        public string DestinationUnitId { get; } = destinationUnitId;
        public TimeSpan StartElapsed { get; } = startElapsed;
        public double Progress;
    }
}
