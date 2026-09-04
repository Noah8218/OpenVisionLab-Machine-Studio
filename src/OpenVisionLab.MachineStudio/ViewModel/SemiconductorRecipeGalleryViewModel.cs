using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Dialogs;

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

public sealed class RecipePackCompatibilityComparisonItemViewModel : ViewModelBase
{
    private readonly RecipePackCompatibilityComparisonItem _comparison;

    internal RecipePackCompatibilityComparisonItemViewModel(
        RecipePackCompatibilityComparisonItem comparison)
    {
        _comparison = comparison;
    }

    public string FileName => _comparison.FileName;
    public string DisplayName => _comparison.DisplayName;
    public bool IsNewlyFailed => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.NewlyFailed;
    public bool IsRecovered => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.Recovered;
    public bool IsAdded => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.Added;
    public bool IsRemoved => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.Removed;
    public bool IsMetadataChanged => _comparison.ChangeKind == RecipePackCompatibilityChangeKind.MetadataChanged;
    public bool HasProjectSchemaChange => _comparison.ProjectSchemaChanged;
    public bool HasBuildChange => _comparison.BuildChanged;
    public string ChangeStatusText => OpenVisionLanguageService.T(
        $"Gallery.Comparison.{_comparison.ChangeKind}");
    public string OutcomeChangeText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.OutcomeFormat"),
        LocalizedOutcome(_comparison.Baseline),
        LocalizedOutcome(_comparison.Current));
    public string ProjectSchemaChangeText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.SchemaFormat"),
        _comparison.Baseline?.ProjectSchema ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"),
        _comparison.Current?.ProjectSchema ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"));
    public string BuildChangeText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.BuildFormat"),
        CompactBuild(_comparison.Baseline),
        CompactBuild(_comparison.Current));
    public string BuildChangeDetailText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.BuildFormat"),
        _comparison.Baseline?.BuildIdentity ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"),
        _comparison.Current?.BuildIdentity ?? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent"));

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(ChangeStatusText));
        OnPropertyChanged(nameof(OutcomeChangeText));
        OnPropertyChanged(nameof(ProjectSchemaChangeText));
        OnPropertyChanged(nameof(BuildChangeText));
        OnPropertyChanged(nameof(BuildChangeDetailText));
    }

    private static string LocalizedOutcome(RecipePackCompatibilityResult? result) =>
        result is null
            ? OpenVisionLanguageService.T("Gallery.Comparison.NotPresent")
            : OpenVisionLanguageService.T(
                result.Outcome == "passed"
                    ? "Gallery.ValidationPassed"
                    : "Gallery.ValidationFailed");

    private static string CompactBuild(RecipePackCompatibilityResult? result)
    {
        if (result is null)
        {
            return OpenVisionLanguageService.T("Gallery.Comparison.NotPresent");
        }

        var commit = result.SourceCommit[..Math.Min(7, result.SourceCommit.Length)];
        return $"{commit} ({result.SourceState})";
    }
}

public sealed class SemiconductorRecipeGalleryViewModel : ViewModelBase
{
    private readonly SemiconductorRecipeGalleryCatalog _catalog = new();
    private readonly SemiconductorRecipeGalleryValidationWorkflow _validationWorkflow = new();
    private readonly Func<SemiconductorRecipeGalleryItemViewModel, string?, Task<bool>> _createCopy;
    private readonly Func<string?> _selectCompatibilityReportSavePath;
    private readonly Func<string?> _selectBaselineCompatibilityReportPath;
    private readonly Func<string?> _selectCurrentCompatibilityReportPath;
    private readonly RelayCommand _closeCommand;
    private readonly AsyncRelayCommand _createCopyCommand;
    private readonly AsyncRelayCommand _validateAllCommand;
    private readonly RelayCommand _saveCompatibilityReportCommand;
    private readonly RelayCommand _compareCompatibilityReportsCommand;
    private readonly RelayCommand _closeCompatibilityComparisonCommand;
    private SemiconductorRecipeGalleryItemViewModel? _selectedItem;
    private RecipePackCompatibilityComparison? _compatibilityComparison;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isComparisonOpen;
    private string _errorMessage = string.Empty;
    private int _validatedCount;
    private int _passedCount;
    private int _failedCount;
    private string _validationProgressText = string.Empty;
    private string _validationSummary = string.Empty;
    private string _firstFailureRecipeName = string.Empty;
    private string _firstFailureStepId = string.Empty;
    private string _firstFailureDetail = string.Empty;
    private string _baselineReportName = string.Empty;
    private string _currentReportName = string.Empty;
    private string _comparisonSummary = string.Empty;
    private string _projectSchemaComparison = string.Empty;

    public SemiconductorRecipeGalleryViewModel(
        Func<SemiconductorRecipeGalleryItemViewModel, string?, Task<bool>> createCopy)
        : this(
            createCopy,
            SemiconductorRecipeCompatibilityDialogHost.SelectSavePath,
            SemiconductorRecipeCompatibilityDialogHost.SelectBaselinePath,
            SemiconductorRecipeCompatibilityDialogHost.SelectCurrentPath)
    {
    }

    internal SemiconductorRecipeGalleryViewModel(
        Func<SemiconductorRecipeGalleryItemViewModel, string?, Task<bool>> createCopy,
        Func<string?> selectCompatibilityReportSavePath,
        Func<string?> selectBaselineCompatibilityReportPath,
        Func<string?> selectCurrentCompatibilityReportPath)
    {
        _createCopy = createCopy;
        _selectCompatibilityReportSavePath = selectCompatibilityReportSavePath ?? throw new ArgumentNullException(nameof(selectCompatibilityReportSavePath));
        _selectBaselineCompatibilityReportPath = selectBaselineCompatibilityReportPath ?? throw new ArgumentNullException(nameof(selectBaselineCompatibilityReportPath));
        _selectCurrentCompatibilityReportPath = selectCurrentCompatibilityReportPath ?? throw new ArgumentNullException(nameof(selectCurrentCompatibilityReportPath));
        OpenCommand = new RelayCommand(_ => Open());
        _closeCommand = new RelayCommand(
            _ => Close(),
            _ => !IsBusy,
            useCommandManagerRequery: false);
        CloseCommand = _closeCommand;
        _createCopyCommand = new AsyncRelayCommand(
            _ => CreateCopyAsync(null),
            _ => SelectedItem is not null && !IsBusy,
            exception => ErrorMessage = exception.Message,
            useCommandManagerRequery: false);
        _validateAllCommand = new AsyncRelayCommand(
            _ => ValidateAllAsync(),
            _ => HasItems && !IsBusy,
            exception => ErrorMessage = exception.Message,
            useCommandManagerRequery: false);
        _saveCompatibilityReportCommand = new RelayCommand(
            _ => SaveCompatibilityReportWithDialog(),
            _ => CanSaveCompatibilityReport,
            useCommandManagerRequery: false);
        _compareCompatibilityReportsCommand = new RelayCommand(
            _ => CompareCompatibilityReportsWithDialogs(),
            _ => !IsBusy,
            useCommandManagerRequery: false);
        _closeCompatibilityComparisonCommand = new RelayCommand(
            _ => CloseCompatibilityComparison(),
            _ => IsComparisonOpen && !IsBusy,
            useCommandManagerRequery: false);
        CreateCopyCommand = _createCopyCommand;
        ValidateAllCommand = _validateAllCommand;
        SaveCompatibilityReportCommand = _saveCompatibilityReportCommand;
        CompareCompatibilityReportsCommand = _compareCompatibilityReportsCommand;
        CloseCompatibilityComparisonCommand = _closeCompatibilityComparisonCommand;
        Reload();
    }

    public ObservableCollection<SemiconductorRecipeGalleryItemViewModel> Items { get; } = new();
    public ObservableCollection<RecipePackCompatibilityComparisonItemViewModel> ComparisonItems { get; } = new();

    public SemiconductorRecipeGalleryItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                _createCopyCommand.RaiseCanExecuteChanged();
                _validateAllCommand.RaiseCanExecuteChanged();
                _closeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                _createCopyCommand.RaiseCanExecuteChanged();
                _validateAllCommand.RaiseCanExecuteChanged();
                _closeCommand.RaiseCanExecuteChanged();
                _saveCompatibilityReportCommand.RaiseCanExecuteChanged();
                _compareCompatibilityReportsCommand.RaiseCanExecuteChanged();
                _closeCompatibilityComparisonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsComparisonOpen
    {
        get => _isComparisonOpen;
        private set
        {
            if (SetProperty(ref _isComparisonOpen, value))
            {
                _closeCompatibilityComparisonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasItems => Items.Count > 0;
    public bool HasSelection => SelectedItem is not null;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public int ValidatedCount
    {
        get => _validatedCount;
        private set => SetProperty(ref _validatedCount, value);
    }

    public int PassedCount
    {
        get => _passedCount;
        private set => SetProperty(ref _passedCount, value);
    }

    public int FailedCount
    {
        get => _failedCount;
        private set => SetProperty(ref _failedCount, value);
    }

    public string ValidationProgressText
    {
        get => _validationProgressText;
        private set => SetProperty(ref _validationProgressText, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set
        {
            if (SetProperty(ref _validationSummary, value))
            {
                OnPropertyChanged(nameof(HasValidationSummary));
            }
        }
    }

    public bool HasValidationSummary => !string.IsNullOrWhiteSpace(ValidationSummary);
    public string FirstFailureRecipeName
    {
        get => _firstFailureRecipeName;
        private set => SetProperty(ref _firstFailureRecipeName, value);
    }

    public string FirstFailureStepId
    {
        get => _firstFailureStepId;
        private set => SetProperty(ref _firstFailureStepId, value);
    }

    public string FirstFailureDetail
    {
        get => _firstFailureDetail;
        private set => SetProperty(ref _firstFailureDetail, value);
    }

    public bool HasFirstFailure => FailedCount > 0;
    public string BaselineReportName => _baselineReportName;
    public string CurrentReportName => _currentReportName;
    public string BaselineReportText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.BaselineFormat"),
        BaselineReportName);
    public string CurrentReportText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Gallery.Comparison.CurrentFormat"),
        CurrentReportName);
    public string ComparisonSummary => _comparisonSummary;
    public string ProjectSchemaComparison => _projectSchemaComparison;
    public bool IsProjectSchemaChanged => _compatibilityComparison?.ProjectSchemaChanged == true;
    public int NewlyFailedCount => CountComparisonItems(RecipePackCompatibilityChangeKind.NewlyFailed);
    public int RecoveredCount => CountComparisonItems(RecipePackCompatibilityChangeKind.Recovered);
    public int MetadataChangedCount => CountComparisonItems(RecipePackCompatibilityChangeKind.MetadataChanged);
    public int AddedCount => CountComparisonItems(RecipePackCompatibilityChangeKind.Added);
    public int RemovedCount => CountComparisonItems(RecipePackCompatibilityChangeKind.Removed);
    public bool CanSaveCompatibilityReport => HasItems
        && ValidatedCount == Items.Count
        && Items.All(item => item.HasValidationResult)
        && !IsBusy;
    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand CreateCopyCommand { get; }
    public ICommand ValidateAllCommand { get; }
    public ICommand SaveCompatibilityReportCommand { get; }
    public ICommand CompareCompatibilityReportsCommand { get; }
    public ICommand CloseCompatibilityComparisonCommand { get; }

    public void Open()
    {
        CloseCompatibilityComparison();
        Reload();
        IsOpen = true;
    }

    public void Close()
    {
        CloseCompatibilityComparison();
        IsOpen = false;
    }

    public void RefreshLocalization()
    {
        if (IsOpen)
        {
            Reload();
            foreach (var item in ComparisonItems)
            {
                item.RefreshLocalization();
            }
            RefreshComparisonText();
        }
        else
        {
            ErrorMessage = string.Empty;
        }
    }

    internal Task<bool> CreateCopyToAsync(string destinationPath) =>
        CreateCopyAsync(destinationPath);

    internal Task ValidateAllForSmokeAsync() => ValidateAllAsync();

    internal void SaveCompatibilityReport(string path) =>
        CreateCompatibilityReport().Save(path);

    internal bool TryCompareCompatibilityReports(string baselinePath, string currentPath)
    {
        try
        {
            ErrorMessage = string.Empty;
            var baseline = RecipePackCompatibilityReport.Load(baselinePath);
            var current = RecipePackCompatibilityReport.Load(currentPath);
            ApplyCompatibilityComparison(
                baseline.CompareTo(current),
                Path.GetFileName(baselinePath),
                Path.GetFileName(currentPath));
            return true;
        }
        catch (NotSupportedException)
        {
            ErrorMessage = OpenVisionLanguageService.T("Gallery.CompatibilityReportUnsupported");
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            ErrorMessage = OpenVisionLanguageService.T("Gallery.CompatibilityReportLoadFailed");
            return false;
        }
    }

    private void Reload()
    {
        var selectedFileName = SelectedItem?.FileName;
        Items.Clear();
        ErrorMessage = string.Empty;
        ResetValidationSummary();

        try
        {
            var galleryPath = Path.Combine(
                AppContext.BaseDirectory,
                "Samples",
                "SemiconductorRecipes");
            foreach (var descriptor in _catalog.Enumerate(galleryPath))
            {
                var item = CreateItem(descriptor);
                item.ResetValidation();
                Items.Add(item);
            }

            SelectedItem = Items.FirstOrDefault(item =>
                string.Equals(item.FileName, selectedFileName, StringComparison.OrdinalIgnoreCase))
                ?? Items.FirstOrDefault();
        }
        catch (Exception)
        {
            SelectedItem = null;
            ErrorMessage = OpenVisionLanguageService.T("Gallery.LoadFailed");
        }

        OnPropertyChanged(nameof(HasItems));
        _validateAllCommand.RaiseCanExecuteChanged();
    }

    private static SemiconductorRecipeGalleryItemViewModel CreateItem(
        SemiconductorRecipeGalleryItemDescriptor descriptor)
    {
        return new SemiconductorRecipeGalleryItemViewModel
        {
            SourcePath = descriptor.SourcePath,
            FileName = descriptor.FileName,
            DisplayName = descriptor.DisplayName,
            ProjectSchema = descriptor.ProjectSchema,
            SequenceName = descriptor.SequenceName ?? OpenVisionLanguageService.T("Shell.NotConfigured"),
            EquipmentFocus = string.Join(
                " · ",
                descriptor.EquipmentFocus.Count > 0
                    ? descriptor.EquipmentFocus
                    : new[] { descriptor.FallbackEquipment }),
            TopologySummary = string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Gallery.TopologySummaryCompact"),
                descriptor.AxisCount,
                descriptor.SensorCount,
                descriptor.CylinderCount,
                descriptor.ConveyorCount,
                descriptor.WorkpieceCount),
            AxisCount = descriptor.AxisCount,
            SensorCount = descriptor.SensorCount,
            CylinderCount = descriptor.CylinderCount,
            ConveyorCount = descriptor.ConveyorCount,
            WorkpieceCount = descriptor.WorkpieceCount,
            DeviceCount = descriptor.DeviceCount,
            ChannelCount = descriptor.ChannelCount,
            ComponentCount = descriptor.ComponentCount,
            StepCount = descriptor.StepCount
        };
    }

    private async Task ValidateAllAsync()
    {
        if (IsBusy || !HasItems)
        {
            return;
        }

        ErrorMessage = string.Empty;
        ResetValidationSummary();
        foreach (var item in Items)
        {
            item.ResetValidation();
        }

        IsBusy = true;
        ValidationProgressText = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.ValidationProgressFormat"),
            0,
            Items.Count);

        try
        {
            foreach (var item in Items)
            {
                item.MarkValidationRunning();
                var validation = await _validationWorkflow.ValidateAsync(item.SourcePath);
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
                bool passed = validation.IsPassed;
                item.MarkValidationCompleted(
                    passed,
                    BuildIdentity.Current,
                    BuildIdentity.Compact,
                    BuildIdentity.SourceCommit,
                    BuildIdentity.SourceState,
                    BuildIdentity.IsExactCommit,
                    failureStep,
                    detail);
                ValidatedCount++;
                if (passed)
                {
                    PassedCount++;
                }
                else
                {
                    FailedCount++;
                    OnPropertyChanged(nameof(HasFirstFailure));
                    if (string.IsNullOrWhiteSpace(FirstFailureRecipeName))
                    {
                        FirstFailureRecipeName = item.DisplayName;
                        FirstFailureStepId = failureStep;
                        FirstFailureDetail = detail;
                        SelectedItem = item;
                    }
                }

                ValidationProgressText = string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Gallery.ValidationProgressFormat"),
                    ValidatedCount,
                    Items.Count);
                _saveCompatibilityReportCommand.RaiseCanExecuteChanged();
            }

            ValidationSummary = FailedCount == 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Gallery.ValidationPassedSummaryFormat"),
                    PassedCount,
                    Items.Count)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Gallery.ValidationFailedSummaryFormat"),
                    FailedCount,
                    FirstFailureRecipeName,
                    FirstFailureStepId,
                    FirstFailureDetail);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetValidationSummary()
    {
        ValidatedCount = 0;
        PassedCount = 0;
        FailedCount = 0;
        OnPropertyChanged(nameof(HasFirstFailure));
        ValidationProgressText = string.Empty;
        ValidationSummary = string.Empty;
        FirstFailureRecipeName = string.Empty;
        FirstFailureStepId = string.Empty;
        FirstFailureDetail = string.Empty;
        _saveCompatibilityReportCommand.RaiseCanExecuteChanged();
    }

    private RecipePackCompatibilityReport CreateCompatibilityReport()
    {
        if (!CanSaveCompatibilityReport)
        {
            throw new InvalidOperationException(
                OpenVisionLanguageService.T("Gallery.CompatibilityReportRequiresValidation"));
        }

        return new RecipePackCompatibilityReport(
            RecipePackCompatibilityReport.CurrentSchema,
            DateTimeOffset.UtcNow,
            MachineProjectDocument.CurrentSchema,
            Items.Select(item => new RecipePackCompatibilityResult(
                    item.FileName,
                    item.DisplayName,
                    item.ProjectSchema,
                    item.ValidationBuildIdentity,
                    item.ValidationSourceCommit,
                    item.ValidationSourceState,
                    item.ValidationIsExactCommit,
                    item.IsValidationPassed ? "passed" : "failed",
                    item.ValidationStepId,
                    item.ValidationDetail))
                .ToArray());
    }

    private void SaveCompatibilityReportWithDialog()
    {
        if (_selectCompatibilityReportSavePath() is not { } path)
        {
            return;
        }

        try
        {
            SaveCompatibilityReport(path);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private void CompareCompatibilityReportsWithDialogs()
    {
        if (_selectBaselineCompatibilityReportPath() is not { } baselinePath)
        {
            return;
        }

        if (_selectCurrentCompatibilityReportPath() is not { } currentPath)
        {
            return;
        }

        TryCompareCompatibilityReports(baselinePath, currentPath);
    }

    private void ApplyCompatibilityComparison(
        RecipePackCompatibilityComparison comparison,
        string baselineReportName,
        string currentReportName)
    {
        _compatibilityComparison = comparison;
        _baselineReportName = baselineReportName;
        _currentReportName = currentReportName;
        ComparisonItems.Clear();
        foreach (var item in comparison.Items)
        {
            ComparisonItems.Add(new RecipePackCompatibilityComparisonItemViewModel(item));
        }

        OnPropertyChanged(nameof(BaselineReportName));
        OnPropertyChanged(nameof(CurrentReportName));
        OnPropertyChanged(nameof(BaselineReportText));
        OnPropertyChanged(nameof(CurrentReportText));
        OnPropertyChanged(nameof(NewlyFailedCount));
        OnPropertyChanged(nameof(RecoveredCount));
        OnPropertyChanged(nameof(MetadataChangedCount));
        OnPropertyChanged(nameof(AddedCount));
        OnPropertyChanged(nameof(RemovedCount));
        OnPropertyChanged(nameof(IsProjectSchemaChanged));
        RefreshComparisonText();
        IsComparisonOpen = true;
    }

    private void CloseCompatibilityComparison()
    {
        _compatibilityComparison = null;
        ComparisonItems.Clear();
        _baselineReportName = string.Empty;
        _currentReportName = string.Empty;
        _comparisonSummary = string.Empty;
        _projectSchemaComparison = string.Empty;
        IsComparisonOpen = false;
        OnPropertyChanged(nameof(BaselineReportName));
        OnPropertyChanged(nameof(CurrentReportName));
        OnPropertyChanged(nameof(BaselineReportText));
        OnPropertyChanged(nameof(CurrentReportText));
        OnPropertyChanged(nameof(ComparisonSummary));
        OnPropertyChanged(nameof(ProjectSchemaComparison));
        OnPropertyChanged(nameof(IsProjectSchemaChanged));
    }

    private void RefreshComparisonText()
    {
        if (_compatibilityComparison is null)
        {
            return;
        }

        _comparisonSummary = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.ComparisonSummaryFormat"),
            NewlyFailedCount,
            RecoveredCount,
            MetadataChangedCount,
            AddedCount,
            RemovedCount);
        _projectSchemaComparison = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.Comparison.ProjectSchemaFormat"),
            _compatibilityComparison.Baseline.CurrentProjectSchema,
            _compatibilityComparison.Current.CurrentProjectSchema);
        OnPropertyChanged(nameof(ComparisonSummary));
        OnPropertyChanged(nameof(ProjectSchemaComparison));
    }

    private int CountComparisonItems(RecipePackCompatibilityChangeKind kind) =>
        _compatibilityComparison?.Items.Count(item => item.ChangeKind == kind) ?? 0;

    private async Task<bool> CreateCopyAsync(string? destinationPath)
    {
        if (SelectedItem is null || IsBusy)
        {
            return false;
        }

        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var created = await _createCopy(SelectedItem, destinationPath);
            if (created)
            {
                Close();
            }

            return created;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
