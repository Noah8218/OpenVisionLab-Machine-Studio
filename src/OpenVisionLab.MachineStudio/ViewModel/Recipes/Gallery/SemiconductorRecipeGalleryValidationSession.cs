using OpenVisionLab.Machine.Simulation.Sequences;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SemiconductorRecipeGalleryItemValidationOutcome(
    SemiconductorRecipeGalleryItemViewModel Item,
    bool IsPassed,
    string FailureStep,
    string Detail);

/// <summary>
/// Runs the gallery's ordered per-item validation and owns the translation
/// from a dry-run result to a gallery item outcome.
/// </summary>
internal sealed class SemiconductorRecipeGalleryValidationSession
{
    private readonly Func<string, Task<SemiconductorRecipeGalleryValidationResult>> _validate;

    internal SemiconductorRecipeGalleryValidationSession()
        : this(CreateDefaultValidator())
    {
    }

    internal SemiconductorRecipeGalleryValidationSession(
        Func<string, Task<SemiconductorRecipeGalleryValidationResult>> validate)
    {
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
    }

    private static Func<string, Task<SemiconductorRecipeGalleryValidationResult>> CreateDefaultValidator()
    {
        var workflow = new SemiconductorRecipeGalleryValidationWorkflow();
        return sourcePath => workflow.ValidateAsync(sourcePath);
    }

    internal async Task ValidateAsync(
        IReadOnlyList<SemiconductorRecipeGalleryItemViewModel> items,
        Action<SemiconductorRecipeGalleryItemValidationOutcome> onItemValidated,
        Action<int, int> onProgress)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(onItemValidated);
        ArgumentNullException.ThrowIfNull(onProgress);

        for (var index = 0; index < items.Count; index++)
        {
            SemiconductorRecipeGalleryItemViewModel item = items[index];
            item.MarkValidationRunning();
            SemiconductorRecipeGalleryValidationResult validation =
                await _validate(item.SourcePath);
            onItemValidated(CreateOutcome(item, validation));
            onProgress(index + 1, items.Count);
        }
    }

    private static SemiconductorRecipeGalleryItemValidationOutcome CreateOutcome(
        SemiconductorRecipeGalleryItemViewModel item,
        SemiconductorRecipeGalleryValidationResult validation)
    {
        string failureStep = validation.FailureStage switch
        {
            SemiconductorRecipeGalleryValidationFailureStage.Load =>
                OpenVisionLanguageService.T("Gallery.ValidationLoadStage"),
            SemiconductorRecipeGalleryValidationFailureStage.SequenceMissing =>
                OpenVisionLanguageService.T("Gallery.ValidationCompileStage"),
            SemiconductorRecipeGalleryValidationFailureStage.Compile
                when string.IsNullOrWhiteSpace(validation.FailureStepId) =>
                OpenVisionLanguageService.T("Gallery.ValidationCompileStage"),
            _ => validation.FailureStepId ?? string.Empty
        };
        string detail = validation.FailureStage
            == SemiconductorRecipeGalleryValidationFailureStage.SequenceMissing
            ? OpenVisionLanguageService.T("Gallery.ValidationSequenceMissing")
            : validation.Detail;
        return new(item, validation.IsPassed, failureStep, detail);
    }
}
