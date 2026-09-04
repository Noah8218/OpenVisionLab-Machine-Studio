using System.IO;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commissioning;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum MultiAxisCommissioningArtifactState
{
    None,
    MemoryOnly,
    Saved,
    Restored,
    StaleRejected,
    SaveFailed
}

internal sealed record MultiAxisCommissioningArtifactContext(
    string ProjectId,
    string ProjectJson,
    TimeSpan FixedStep,
    MultiAxisCommissioningRecipeDefinition Recipe);

/// <summary>
/// Owns project-linked multi-axis commissioning artifacts without depending on
/// WPF or the commissioning ViewModel.
/// </summary>
internal sealed class MultiAxisCommissioningArtifactStore
{
    private DeterministicMultiAxisCommissioningResultPackage? _latestResult;
    private DeterministicMultiAxisCommissioningBaseline? _acceptedBaseline;
    private DeterministicMultiAxisCommissioningResultHistory _history =
        DeterministicMultiAxisCommissioningResultHistory.Empty(string.Empty);

    internal DeterministicMultiAxisCommissioningResultPackage? LatestResult => _latestResult;

    internal DeterministicMultiAxisCommissioningBaseline? AcceptedBaseline => _acceptedBaseline;

    internal DeterministicMultiAxisCommissioningResultHistory History => _history;

    internal MultiAxisCommissioningArtifactState State { get; private set; }

    internal bool HasRestoredResult => State == MultiAxisCommissioningArtifactState.Restored
        && _latestResult is not null;

    internal void Reset(string projectId)
    {
        _latestResult = null;
        _acceptedBaseline = null;
        _history = DeterministicMultiAxisCommissioningResultHistory.Empty(projectId);
        State = MultiAxisCommissioningArtifactState.None;
    }

    internal void SetLatestResult(DeterministicMultiAxisCommissioningResultPackage? result) =>
        _latestResult = result;

    internal void SetAcceptedBaseline(DeterministicMultiAxisCommissioningBaseline? baseline) =>
        _acceptedBaseline = baseline;

    internal void AppendHistory(
        DeterministicMultiAxisCommissioningResultPackage result,
        DateTimeOffset capturedAtUtc) =>
        _history = _history.Append(result, capturedAtUtc);

    internal void Restore(
        string projectId,
        string? projectPath,
        Func<MultiAxisCommissioningArtifactContext?> createContext)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        _latestResult = null;
        _acceptedBaseline = null;
        _history = DeterministicMultiAxisCommissioningResultHistory.Empty(projectId);
        if (projectPath is null)
        {
            State = MultiAxisCommissioningArtifactState.None;
            return;
        }

        var resultPath = CommissioningResultArtifactPath(projectPath);
        var history = DeterministicMultiAxisCommissioningResultHistory.LoadFromJson(
            CommissioningHistoryArtifactPath(projectPath));
        if (history is { } restoredHistory
            && restoredHistory.HasValidEvidenceHash()
            && string.Equals(restoredHistory.ProjectId, projectId, StringComparison.Ordinal))
        {
            _history = restoredHistory;
        }

        var baseline = DeterministicMultiAxisCommissioningBaseline.LoadFromJson(
            CommissioningBaselineArtifactPath(projectPath));
        if (baseline?.HasValidEvidenceHash() == true
            && string.Equals(baseline.ProjectId, projectId, StringComparison.Ordinal))
        {
            _acceptedBaseline = baseline;
        }

        if (!File.Exists(resultPath))
        {
            State = _history.Entries.IsDefaultOrEmpty && _acceptedBaseline is null
                ? MultiAxisCommissioningArtifactState.None
                : MultiAxisCommissioningArtifactState.Restored;
            return;
        }

        var context = createContext();
        if (context is not null
            && (DeterministicMultiAxisCommissioningResultPackage.LoadFromJson(resultPath) is { } result)
            && result.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.FixedStep,
                context.Recipe))
        {
            _latestResult = result;
            State = MultiAxisCommissioningArtifactState.Restored;
        }
        else
        {
            State = MultiAxisCommissioningArtifactState.StaleRejected;
        }
    }

    internal void RelinkProjectPath(string projectPath)
    {
        if (_latestResult is not null)
        {
            _latestResult = _latestResult with
            {
                ProjectPath = Path.GetFullPath(projectPath)
            };
        }
    }

    internal string? Persist(
        string? projectPath,
        Func<MultiAxisCommissioningArtifactContext?> createContext)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        if (_latestResult is null)
        {
            State = MultiAxisCommissioningArtifactState.None;
            return null;
        }

        if (projectPath is null)
        {
            State = MultiAxisCommissioningArtifactState.MemoryOnly;
            return null;
        }

        var context = createContext();
        if (context is null
            || !_latestResult.IsForContext(
                context.ProjectId,
                context.ProjectJson,
                context.FixedStep,
                context.Recipe))
        {
            State = MultiAxisCommissioningArtifactState.StaleRejected;
            return null;
        }

        try
        {
            DeterministicMultiAxisCommissioningResultPackage.SaveToJson(
                _latestResult,
                CommissioningResultArtifactPath(projectPath));
            DeterministicMultiAxisCommissioningResultHistory.SaveToJson(
                _history,
                CommissioningHistoryArtifactPath(projectPath));
            if (_acceptedBaseline is not null)
            {
                DeterministicMultiAxisCommissioningBaseline.SaveToJson(
                    _acceptedBaseline,
                    CommissioningBaselineArtifactPath(projectPath));
            }
            State = MultiAxisCommissioningArtifactState.Saved;
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            State = MultiAxisCommissioningArtifactState.SaveFailed;
            return exception.Message;
        }
    }

    internal string? ClearBaseline(string? projectPath)
    {
        _acceptedBaseline = null;
        if (projectPath is null)
        {
            return null;
        }

        try
        {
            File.Delete(CommissioningBaselineArtifactPath(projectPath));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            State = MultiAxisCommissioningArtifactState.SaveFailed;
            return exception.Message;
        }
    }

    internal void InvalidateContextIfResult()
    {
        if (_latestResult is not null)
        {
            State = MultiAxisCommissioningArtifactState.StaleRejected;
        }
    }

    private static string CommissioningResultArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.commissioning-result.json";

    private static string CommissioningHistoryArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.commissioning-history.json";

    private static string CommissioningBaselineArtifactPath(string projectPath) =>
        $"{Path.GetFullPath(projectPath)}.commissioning-baseline.json";
}
