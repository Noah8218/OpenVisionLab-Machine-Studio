using System.Text.Json;
using System.Threading.Channels;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.History;

public interface ISimulationRunRecordSource
{
    ChannelReader<MachineRunRecord> RunRecordReader { get; }
    string? ActiveRunId { get; }
}

/// <summary>Collects on the simulation worker before UI snapshot throttling; never performs file or UI work.</summary>
internal sealed class SimulationRunRecorder
{
    private readonly Channel<MachineRunRecord> _completed = Channel.CreateUnbounded<MachineRunRecord>(new() { SingleWriter = true, SingleReader = true });
    private readonly Dictionary<string, RunProductRecord> _products = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunCaptureRecord> _captures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lastAcquisitions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sequenceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previousExits = new(StringComparer.Ordinal);
    private RunRecordingContext? _context;
    private MachineRunRecord? _active;
    private SimulationSnapshot? _last;
    private bool _manual;

    internal ChannelReader<MachineRunRecord> Reader => _completed.Reader;
    internal string? ActiveRunId => Volatile.Read(ref _active)?.Id;

    internal void Configure(RunRecordingContext? context)
    {
        Finish("Stopped");
        _context = context;
        _lastAcquisitions.Clear();
        _last = null;
    }

    internal void Observe(SimulationSnapshot snapshot)
    {
        var sequenceRunning = snapshot.Sequences.Any(sequence => sequence.Status == SequenceExecutionStatus.Running) || snapshot.AutomaticRun.IsActive;
        var manualRunning = snapshot.ControlOwner == SimulationControlOwner.Manual && snapshot.RunMode == SimulationRunMode.RealTime;
        var running = sequenceRunning || manualRunning;
        if (_active is null && running && _context is not null)
        {
            _products.Clear();
            _captures.Clear();
            _sequenceIds.Clear();
            _previousExits.Clear();
            foreach (var exit in _last?.UnitMaterialExits ?? []) _previousExits.Add(exit.InstanceId);
            _manual = manualRunning && !sequenceRunning;
            _active = new()
            {
                Id = Guid.NewGuid().ToString("N"), RecipeId = _context.RecipeId, RecipeName = _context.RecipeName,
                AppliedRecipeJson = _context.AppliedRecipeJson, BuildIdentity = _context.BuildIdentity, ProjectPath = _context.ProjectPath,
                StartedAtUtc = DateTimeOffset.UtcNow, StartedSimulationSeconds = snapshot.SimulationTime.TotalSeconds,
                Scope = string.Join(", ", snapshot.Sequences.Where(sequence => sequence.Status == SequenceExecutionStatus.Running).Select(sequence => sequence.SequenceId)),
                RuntimeSettingsJson = JsonSerializer.Serialize(new { snapshot.TimeScale, snapshot.ConditionScenario, snapshot.Faults }),
                TargetFeedCount = snapshot.MaterialSupply?.TargetCount
            };
        }
        if (_active is not null)
        {
            foreach (var sequence in snapshot.Sequences.Where(sequence => sequence.Status == SequenceExecutionStatus.Running)) _sequenceIds.Add(sequence.SequenceId);
            foreach (var slot in snapshot.UnitMaterialSlots.Where(slot => slot.InstanceId is not null))
            {
                var previous = _products.GetValueOrDefault(slot.InstanceId!);
                _products[slot.InstanceId!] = new(slot.InstanceId!, slot.WorkpieceName, slot.UnitId, slot.State, slot.Verdict, null,
                    previous?.FirstObservedTick ?? snapshot.TickIndex, snapshot.TickIndex);
            }
            foreach (var exit in snapshot.UnitMaterialExits.Where(exit => !_previousExits.Contains(exit.InstanceId)))
            {
                var previous = _products.GetValueOrDefault(exit.InstanceId);
                _products[exit.InstanceId] = new(exit.InstanceId, previous?.Name ?? string.Empty, exit.UnitId, "Exited", exit.Verdict, exit.Route,
                    previous?.FirstObservedTick ?? snapshot.TickIndex, snapshot.TickIndex);
            }
            foreach (var piece in snapshot.Workpieces)
                _products[piece.Id] = new(piece.Id, piece.Name, null, piece.State.ToString(), null, null,
                    _products.GetValueOrDefault(piece.Id)?.FirstObservedTick ?? snapshot.TickIndex, snapshot.TickIndex, "Component");
            foreach (var camera in snapshot.Cameras)
                RecordCapture(camera, snapshot.TickIndex);
            if (snapshot.TickIndex >= (_last?.TickIndex ?? 0)) _last = snapshot;
            if (!running && !_manual)
            {
                var participating = snapshot.Sequences.Where(sequence => _sequenceIds.Contains(sequence.SequenceId)).ToArray();
                var faulted = participating.Any(sequence => sequence.Status == SequenceExecutionStatus.Faulted);
                Finish(faulted ? "Faulted" : participating.Length > 0 && participating.All(sequence => sequence.Status == SequenceExecutionStatus.Completed) ? "Completed" : "Stopped");
            }
        }
        else _last = snapshot;
        foreach (var camera in snapshot.Cameras)
            if (camera.Result?.AcquisitionId is { } id) _lastAcquisitions.Add(id);
    }

    internal void Complete(bool faulted)
    {
        Finish(faulted ? "Faulted" : "Stopped");
        _completed.Writer.TryComplete();
    }

    internal void Stop()
    {
        Finish("Stopped");
        _lastAcquisitions.Clear();
        _last = null;
    }

    internal void RecordCapture(VirtualCameraSnapshot camera, long tick)
    {
        if (_active is null || camera.State is not (VirtualCameraState.FrameReady or VirtualCameraState.AwaitingExternalResult)) return;
        var acquisitionId = camera.Result?.AcquisitionId ?? camera.CurrentAcquisitionId;
        if (acquisitionId is null || _lastAcquisitions.Contains(acquisitionId) && !_captures.ContainsKey(acquisitionId)) return;
        var input = camera.Result?.NumericalInput ?? camera.NumericalInput;
        var frame = camera.Result?.FrameEvidence ?? camera.FrameEvidence ?? input?.Frame;
        if (frame is null && camera.Result is null) return;
        var previous = _captures.GetValueOrDefault(acquisitionId);
        _captures[acquisitionId] = new()
        {
            AcquisitionId = acquisitionId, CameraId = camera.Id, ProductId = camera.Result?.WorkpieceInstanceId ?? camera.Result?.WorkpieceComponentId ?? previous?.ProductId,
            FrameId = frame?.FrameId, SourceRelativePath = frame?.SourceRelativePath, ContentSha256 = frame?.ContentSha256,
            Width = frame?.Width, Height = frame?.Height, PixelFormat = frame?.PixelFormat,
            Pixels = previous?.Pixels ?? input?.CopyBytes(), PreviewGray8 = previous?.PreviewGray8 ?? input?.CopyPreview(),
            Verdict = camera.Result is null ? null : camera.Result.Decision == OpenVisionLab.Machine.Core.Devices.PlaceholderInspectionDecision.Pass ? "OK" : "NG",
            ResultJson = camera.Result is null ? previous?.ResultJson : JsonSerializer.Serialize(new { camera.Result.InspectionEvidence, camera.Result.ExternalResultEvidence, camera.Result.Decision }),
            Tick = previous?.Tick ?? tick
        };
    }

    private void Finish(string outcome)
    {
        if (_active is null) return;
        _completed.Writer.TryWrite(_active with
        {
            Outcome = outcome, EndedAtUtc = DateTimeOffset.UtcNow, EndedSimulationSeconds = _last?.SimulationTime.TotalSeconds ?? _active.StartedSimulationSeconds,
            ActualFeedCount = _last?.MaterialSupply?.FedCount, Products = _products.Values.ToArray(), Captures = _captures.Values.ToArray(),
            Scope = string.Join(", ", _sequenceIds),
            Faults = _last?.Sequences.Where(sequence => _sequenceIds.Contains(sequence.SequenceId) && sequence.Status == SequenceExecutionStatus.Faulted).Select(sequence => sequence.LastError?.Message ?? sequence.LastError?.Code.ToString() ?? "Faulted").ToArray() ?? []
        });
        _active = null;
        _manual = false;
    }
}
