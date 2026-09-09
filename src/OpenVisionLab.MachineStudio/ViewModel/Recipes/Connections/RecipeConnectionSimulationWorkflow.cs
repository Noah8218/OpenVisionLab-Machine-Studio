using System.IO;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the Recipe Connection simulation preflight and isolated preview
/// presentation policy. It does not own the live runtime or any WPF state.
/// </summary>
internal sealed class RecipeConnectionSimulationWorkflow
{
    private readonly Func<MachineProjectDocument> _projectProvider;
    private readonly Action<MachineProjectDocument> _validateRuntimeConfiguration;
    private readonly Action<string> _setStatus;
    private readonly Action<string, string> _log;
    private readonly DeterministicSequenceStepPreviewRunner _sequenceStepPreviewRunner = new();
    private readonly DeterministicRecipeDryRunRunner _recipeDryRunRunner = new();

    internal RecipeConnectionSimulationWorkflow(
        Func<MachineProjectDocument> projectProvider,
        Action<MachineProjectDocument> validateRuntimeConfiguration,
        Action<string> setStatus,
        Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(projectProvider);
        ArgumentNullException.ThrowIfNull(validateRuntimeConfiguration);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(log);
        _projectProvider = projectProvider;
        _validateRuntimeConfiguration = validateRuntimeConfiguration;
        _setStatus = setStatus;
        _log = log;
    }

    internal string? ValidateSimulationReadiness()
    {
        try
        {
            _validateRuntimeConfiguration(_projectProvider());
            _setStatus(OpenVisionLanguageService.T("Connections.ReadinessPassedStatus"));
            _log(
                "Project",
                "Simulation readiness validation passed without applying or running the runtime");
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            _setStatus(OpenVisionLanguageService.T("Connections.ReadinessFailedStatus"));
            _log("Project", $"Simulation readiness validation rejected · {exception.Message}");
            return exception.Message;
        }
    }

    internal async Task<SequenceStepPreviewResult> RunSequenceStepPreviewAsync(
        string sequenceId,
        string stepId,
        string componentId)
    {
        var result = await _sequenceStepPreviewRunner.RunAsync(
            _projectProvider(),
            sequenceId,
            stepId,
            componentId);
        _setStatus(OpenVisionLanguageService.T(result.IsCompleted
            ? "Connections.PreviewCompletedStatus"
            : "Connections.PreviewStoppedStatus"));
        _log(
            "Simulation",
            $"Isolated connection step preview · {sequenceId}/{stepId} · {result.Outcome} · {result.ExecutedTicks}/{result.MaximumTicks} ticks");
        return result;
    }

    internal async Task<RecipeDryRunResult> RunRecipeDryRunAsync(string sequenceId)
    {
        var result = await _recipeDryRunRunner.RunAsync(_projectProvider(), sequenceId);
        _setStatus(OpenVisionLanguageService.T(result.IsCompleted
            ? "Connections.DryRunCompletedStatus"
            : "Connections.DryRunStoppedStatus"));
        _log(
            "Simulation",
            $"Isolated recipe dry run · {sequenceId} · {result.Outcome} · {result.ExecutedTicks}/{result.MaximumTicks} ticks · {result.Timeline.Count} steps");
        return result;
    }
}
