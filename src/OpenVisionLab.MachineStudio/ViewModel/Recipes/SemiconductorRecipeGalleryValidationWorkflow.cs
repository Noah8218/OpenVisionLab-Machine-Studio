using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Sequences;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum SemiconductorRecipeGalleryValidationFailureStage
{
    None,
    SequenceMissing,
    Compile,
    Load
}

internal sealed record SemiconductorRecipeGalleryValidationResult(
    RecipeDryRunResult? DryRunResult,
    SemiconductorRecipeGalleryValidationFailureStage FailureStage,
    string? FailureStepId,
    string Detail)
{
    internal bool IsPassed => DryRunResult?.Outcome == RecipeDryRunOutcome.Completed;
}

internal sealed class SemiconductorRecipeGalleryValidationWorkflow
{
    private readonly ProjectDocumentStore _projectStore = new();
    private readonly DeterministicRecipeDryRunRunner _dryRunRunner = new();

    internal async Task<SemiconductorRecipeGalleryValidationResult> ValidateAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var project = await _projectStore.LoadAsync(sourcePath, cancellationToken);
            string? sequenceId = project.Simulation.AutomaticRun?.SequenceId;
            if (string.IsNullOrWhiteSpace(sequenceId))
            {
                return new(
                    null,
                    SemiconductorRecipeGalleryValidationFailureStage.SequenceMissing,
                    null,
                    string.Empty);
            }

            var result = await _dryRunRunner.RunAsync(project, sequenceId, cancellationToken: cancellationToken);
            var failureStepId = result.FirstIssue?.StepId
                ?? result.FirstCheckpointMismatch?.StepId
                ?? (result.Outcome == RecipeDryRunOutcome.Rejected
                    ? null
                    : result.Timeline.LastOrDefault()?.StepId ?? sequenceId);
            var failureStage = result.Outcome == RecipeDryRunOutcome.Rejected
                ? SemiconductorRecipeGalleryValidationFailureStage.Compile
                : SemiconductorRecipeGalleryValidationFailureStage.None;

            return new(result, failureStage, failureStepId, result.Detail);
        }
        catch (Exception exception)
        {
            return new(
                null,
                SemiconductorRecipeGalleryValidationFailureStage.Load,
                null,
                exception.Message);
        }
    }
}
