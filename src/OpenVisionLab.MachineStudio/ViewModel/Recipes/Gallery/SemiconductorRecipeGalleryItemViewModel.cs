using System.Globalization;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class SemiconductorRecipeGalleryItemViewModel : ViewModelBase
{
    private string _validationStatusText = string.Empty;
    private bool _isValidationRunning;
    private bool _isValidationPassed;
    private bool _isValidationFailed;
    private string _validationBuildIdentity = string.Empty;
    private string _validationBuildCompactIdentity = string.Empty;
    private string _validationSourceCommit = string.Empty;
    private string _validationSourceState = string.Empty;
    private bool _validationIsExactCommit;
    private string _validationStepId = string.Empty;
    private string _validationDetail = string.Empty;

    public required string SourcePath { get; init; }
    public required string FileName { get; init; }
    public required string DisplayName { get; init; }
    public required string ProjectSchema { get; init; }
    public required string SequenceName { get; init; }
    public required string EquipmentFocus { get; init; }
    public required string TopologySummary { get; init; }
    public required int AxisCount { get; init; }
    public required int SensorCount { get; init; }
    public required int CylinderCount { get; init; }
    public required int ConveyorCount { get; init; }
    public required int WorkpieceCount { get; init; }
    public required int DeviceCount { get; init; }
    public required int ChannelCount { get; init; }
    public required int ComponentCount { get; init; }
    public required int StepCount { get; init; }

    public string ValidationStatusText
    {
        get => _validationStatusText;
        private set => SetProperty(ref _validationStatusText, value);
    }

    public bool IsValidationRunning
    {
        get => _isValidationRunning;
        private set => SetProperty(ref _isValidationRunning, value);
    }

    public bool IsValidationPassed
    {
        get => _isValidationPassed;
        private set => SetProperty(ref _isValidationPassed, value);
    }

    public bool IsValidationFailed
    {
        get => _isValidationFailed;
        private set => SetProperty(ref _isValidationFailed, value);
    }

    public bool HasValidationResult => IsValidationPassed || IsValidationFailed;
    public string ValidationBuildIdentity => _validationBuildIdentity;
    public string ValidationSourceCommit => _validationSourceCommit;
    public string ValidationSourceState => _validationSourceState;
    public bool ValidationIsExactCommit => _validationIsExactCommit;
    public string ValidationStepId => _validationStepId;
    public string ValidationDetail => _validationDetail;
    public string ValidationContextCompactText => HasValidationResult
        ? $"{ProjectSchema} · {_validationBuildCompactIdentity}"
        : ProjectSchema;
    public string ValidationContextDetailText => HasValidationResult
        ? string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.ValidationContextFormat"),
            ProjectSchema,
            ValidationBuildIdentity)
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.ProjectSchemaFormat"),
            ProjectSchema);

    internal void ResetValidation()
    {
        ValidationStatusText = OpenVisionLanguageService.T("Gallery.ValidationPending");
        IsValidationRunning = false;
        IsValidationPassed = false;
        IsValidationFailed = false;
        _validationBuildIdentity = string.Empty;
        _validationBuildCompactIdentity = string.Empty;
        _validationSourceCommit = string.Empty;
        _validationSourceState = string.Empty;
        _validationIsExactCommit = false;
        _validationStepId = string.Empty;
        _validationDetail = string.Empty;
        RaiseValidationContextChanged();
    }

    internal void MarkValidationRunning()
    {
        ValidationStatusText = OpenVisionLanguageService.T("Gallery.ValidationRunning");
        IsValidationRunning = true;
        IsValidationPassed = false;
        IsValidationFailed = false;
    }

    internal void MarkValidationCompleted(
        bool passed,
        string buildIdentity,
        string compactBuildIdentity,
        string sourceCommit,
        string sourceState,
        bool isExactCommit,
        string stepId,
        string detail)
    {
        ValidationStatusText = OpenVisionLanguageService.T(
            passed ? "Gallery.ValidationPassed" : "Gallery.ValidationFailed");
        IsValidationRunning = false;
        IsValidationPassed = passed;
        IsValidationFailed = !passed;
        _validationBuildIdentity = buildIdentity;
        _validationBuildCompactIdentity = compactBuildIdentity;
        _validationSourceCommit = sourceCommit;
        _validationSourceState = sourceState;
        _validationIsExactCommit = isExactCommit;
        _validationStepId = stepId;
        _validationDetail = detail;
        RaiseValidationContextChanged();
    }

    private void RaiseValidationContextChanged()
    {
        OnPropertyChanged(nameof(HasValidationResult));
        OnPropertyChanged(nameof(ValidationBuildIdentity));
        OnPropertyChanged(nameof(ValidationSourceCommit));
        OnPropertyChanged(nameof(ValidationSourceState));
        OnPropertyChanged(nameof(ValidationIsExactCommit));
        OnPropertyChanged(nameof(ValidationStepId));
        OnPropertyChanged(nameof(ValidationDetail));
        OnPropertyChanged(nameof(ValidationContextCompactText));
        OnPropertyChanged(nameof(ValidationContextDetailText));
    }
}
