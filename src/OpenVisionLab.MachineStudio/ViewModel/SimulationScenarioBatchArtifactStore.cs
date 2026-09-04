using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using OpenVisionLab.Machine.Simulation.Scenarios;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum SimulationScenarioBatchArtifactState
{
    None,
    MemoryOnly,
    Saved,
    Restored,
    Imported,
    StaleRejected,
    SaveFailed
}

internal sealed record SimulationScenarioBatchArtifactContext(
    string ProjectId,
    string ProjectJson,
    TimeSpan FixedStep,
    DeterministicConditionScenarioProfile Profile,
    int RepetitionCount,
    string BuildIdentity)
{
    internal string BatchId => $"{ProjectId}:{Profile.ScenarioId}";
}

/// <summary>
/// Owns project-linked deterministic batch artifacts without depending on WPF
/// or the scenario batch ViewModel.
/// </summary>
internal sealed class SimulationScenarioBatchArtifactStore
{
    private DeterministicSimulationBatchResultPackage? _latestBatchResult;
    private DeterministicSimulationRunResultPackage? _acceptedBatchBaseline;

    internal DeterministicSimulationBatchResultPackage? LatestBatchResult => _latestBatchResult;

    internal DeterministicSimulationRunResultPackage? AcceptedBatchBaseline => _acceptedBatchBaseline;

    internal SimulationScenarioBatchArtifactState State { get; private set; }

    internal bool HasRestoredArtifacts => State == SimulationScenarioBatchArtifactState.Restored
        && _latestBatchResult is not null
        && _acceptedBatchBaseline is not null;

    internal void Reset()
    {
        _latestBatchResult = null;
        _acceptedBatchBaseline = null;
        State = SimulationScenarioBatchArtifactState.None;
    }

    internal void SetLatestBatchResult(DeterministicSimulationBatchResultPackage? batchResult) =>
        _latestBatchResult = batchResult;

    internal void SetAcceptedBatchBaseline(DeterministicSimulationRunResultPackage? baseline) =>
        _acceptedBatchBaseline = baseline;

    internal void Restore(
        string? projectPath,
        Func<SimulationScenarioBatchArtifactContext?> createContext)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        _latestBatchResult = null;
        _acceptedBatchBaseline = null;
        if (projectPath is null)
        {
            State = SimulationScenarioBatchArtifactState.None;
            return;
        }

        var resultPath = ResultArtifactPath(projectPath);
        var baselinePath = BaselineArtifactPath(projectPath);
        var hasResultFile = File.Exists(resultPath);
        var hasBaselineFile = File.Exists(baselinePath);
        if (!hasResultFile && !hasBaselineFile)
        {
            State = SimulationScenarioBatchArtifactState.None;
            return;
        }

        var context = createContext();
        if (context is null)
        {
            State = SimulationScenarioBatchArtifactState.StaleRejected;
            return;
        }

        var rejected = false;
        var restored = false;
        var result = DeterministicSimulationBatchResultPackage.LoadFromJson(resultPath);
        if (result is not null
            && result.IsForContext(
                context.BatchId,
                context.BuildIdentity,
                context.RepetitionCount,
                context.ProjectId,
                context.ProjectJson,
                context.FixedStep,
                context.Profile))
        {
            _latestBatchResult = result;
            restored = true;
        }
        else if (hasResultFile)
        {
            rejected = true;
        }

        var baseline = DeterministicSimulationRunResultPackage.LoadFromJson(baselinePath);
        if (baseline is not null
            && baseline.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.FixedStep,
                context.Profile))
        {
            _acceptedBatchBaseline = baseline;
            restored = true;
        }
        else if (hasBaselineFile)
        {
            rejected = true;
        }

        State = rejected
            ? SimulationScenarioBatchArtifactState.StaleRejected
            : restored
                ? SimulationScenarioBatchArtifactState.Restored
                : SimulationScenarioBatchArtifactState.None;
    }

    internal void RelinkProjectPath(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        if (_acceptedBatchBaseline is not null)
        {
            _acceptedBatchBaseline = _acceptedBatchBaseline with { ProjectPath = fullPath };
        }

        if (_latestBatchResult is not null)
        {
            _latestBatchResult = _latestBatchResult with
            {
                Runs = _latestBatchResult.Runs
                    .Select(run => run with
                    {
                        Result = run.Result with { ProjectPath = fullPath }
                    })
                    .ToImmutableArray()
            };
        }
    }

    internal string? Persist(
        string? projectPath,
        Func<SimulationScenarioBatchArtifactContext?> createContext)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        if (_latestBatchResult is null && _acceptedBatchBaseline is null)
        {
            State = SimulationScenarioBatchArtifactState.None;
            return null;
        }

        if (projectPath is null)
        {
            State = SimulationScenarioBatchArtifactState.MemoryOnly;
            return null;
        }

        var context = createContext();
        if (context is null)
        {
            State = SimulationScenarioBatchArtifactState.StaleRejected;
            return null;
        }

        var saved = false;
        try
        {
            if (_latestBatchResult is not null
                && _latestBatchResult.IsForContext(
                    context.BatchId,
                    context.BuildIdentity,
                    context.RepetitionCount,
                    context.ProjectId,
                    context.ProjectJson,
                    context.FixedStep,
                    context.Profile))
            {
                DeterministicSimulationBatchResultPackage.SaveToJson(
                    _latestBatchResult,
                    ResultArtifactPath(projectPath));
                saved = true;
            }

            if (_acceptedBatchBaseline is not null
                && _acceptedBatchBaseline.IsForContext(
                    context.ProjectId,
                    context.ProjectJson,
                    context.FixedStep,
                    context.Profile))
            {
                DeterministicSimulationRunResultPackage.SaveToJson(
                    _acceptedBatchBaseline,
                    BaselineArtifactPath(projectPath));
                saved = true;
            }

            State = saved
                ? SimulationScenarioBatchArtifactState.Saved
                : SimulationScenarioBatchArtifactState.StaleRejected;
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            State = SimulationScenarioBatchArtifactState.SaveFailed;
            return exception.Message;
        }
    }

    internal string? ClearBaseline(string? projectPath)
    {
        _acceptedBatchBaseline = null;
        if (projectPath is not null)
        {
            try
            {
                var baselinePath = BaselineArtifactPath(projectPath);
                if (File.Exists(baselinePath))
                {
                    File.Delete(baselinePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                State = SimulationScenarioBatchArtifactState.SaveFailed;
                return exception.Message;
            }
        }

        State = _latestBatchResult is null
            ? SimulationScenarioBatchArtifactState.None
            : projectPath is null
                ? SimulationScenarioBatchArtifactState.MemoryOnly
                : SimulationScenarioBatchArtifactState.Saved;
        return null;
    }

    internal void SetImportedPackages(
        DeterministicSimulationBatchResultPackage batchResult,
        DeterministicSimulationRunResultPackage? acceptedBaseline)
    {
        _latestBatchResult = batchResult ?? throw new ArgumentNullException(nameof(batchResult));
        _acceptedBatchBaseline = acceptedBaseline;
        State = SimulationScenarioBatchArtifactState.Imported;
    }

    internal bool TryExportEvidence(
        string path,
        out string evidenceHash,
        out string? errorDetail)
    {
        evidenceHash = string.Empty;
        errorDetail = null;
        if (_latestBatchResult is null)
        {
            errorDetail = "No complete batch evidence is available.";
            return false;
        }

        try
        {
            var exchange = DeterministicSimulationEvidenceExchangePackage.Create(
                _latestBatchResult,
                _acceptedBatchBaseline);
            DeterministicSimulationEvidenceExchangePackage.SaveToJson(exchange, path);
            evidenceHash = exchange.EvidenceHash;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errorDetail = exception.Message;
            return false;
        }
    }

    internal bool TryImportEvidence(
        string path,
        string? projectPath,
        Func<SimulationScenarioBatchArtifactContext?> createContext,
        out string evidenceHash,
        out string? rejectionDetail)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        evidenceHash = string.Empty;
        rejectionDetail = null;
        var exchange = DeterministicSimulationEvidenceExchangePackage.LoadFromJson(path);
        if (exchange is null)
        {
            rejectionDetail = "file could not be loaded";
            return false;
        }

        try
        {
            var context = createContext();
            if (context is null)
            {
                rejectionDetail = "file could not be loaded";
                return false;
            }

            if (!exchange.IsForContext(
                    context.ProjectId,
                    context.ProjectJson,
                    context.FixedStep,
                    context.Profile,
                    context.BuildIdentity))
            {
                rejectionDetail = "context mismatch";
                return false;
            }

            var targetProjectPath = projectPath
                ?? Path.Combine(AppContext.BaseDirectory, $"unsaved-{context.ProjectId}.ovmachine");
            if (!exchange.TryGetPackages(
                    targetProjectPath,
                    out var batchResult,
                    out var acceptedBaseline))
            {
                rejectionDetail = "package validation failed";
                return false;
            }

            SetImportedPackages(batchResult, acceptedBaseline);
            evidenceHash = exchange.EvidenceHash;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or JsonException)
        {
            rejectionDetail = exception.Message;
            return false;
        }
    }

    private static string ResultArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.batch-result.json";

    private static string BaselineArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.batch-baseline.json";
}
