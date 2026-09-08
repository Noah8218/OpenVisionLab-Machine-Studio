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

public sealed class SemiconductorRecipeGalleryViewModel : ViewModelBase, IDisposable
{
    private readonly SemiconductorRecipeGalleryCatalog _catalog = new();
    private readonly SemiconductorRecipeGalleryValidationSession _validationSession = new();
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
    private int _disposed;
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
        OpenCommand = new RelayCommand(_ => Open(), _ => !IsDisposed);
        _closeCommand = new RelayCommand(
            _ => Close(),
            _ => !IsDisposed && !IsBusy,
            useCommandManagerRequery: false);
        CloseCommand = _closeCommand;
        _createCopyCommand = new AsyncRelayCommand(
            _ => CreateCopyAsync(null),
            _ => !IsDisposed && SelectedItem is not null && !IsBusy,
            exception => ErrorMessage = exception.Message,
            useCommandManagerRequery: false);
        _validateAllCommand = new AsyncRelayCommand(
            _ => ValidateAllAsync(),
            _ => !IsDisposed && HasItems && !IsBusy,
            exception => ErrorMessage = exception.Message,
            useCommandManagerRequery: false);
        _saveCompatibilityReportCommand = new RelayCommand(
            _ => SaveCompatibilityReportWithDialog(),
            _ => !IsDisposed && CanSaveCompatibilityReport,
            useCommandManagerRequery: false);
        _compareCompatibilityReportsCommand = new RelayCommand(
            _ => CompareCompatibilityReportsWithDialogs(),
            _ => !IsDisposed && !IsBusy,
            useCommandManagerRequery: false);
        _closeCompatibilityComparisonCommand = new RelayCommand(
            _ => CloseCompatibilityComparison(),
            _ => !IsDisposed && IsComparisonOpen && !IsBusy,
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
            if (IsDisposed)
            {
                return;
            }

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
            if (IsDisposed)
            {
                return;
            }

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
            if (IsDisposed)
            {
                return;
            }

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
        && !IsBusy
        && !IsDisposed;
    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand CreateCopyCommand { get; }
    public ICommand ValidateAllCommand { get; }
    public ICommand SaveCompatibilityReportCommand { get; }
    public ICommand CompareCompatibilityReportsCommand { get; }
    public ICommand CloseCompatibilityComparisonCommand { get; }

    public void Open()
    {
        if (IsDisposed)
        {
            return;
        }

        CloseCompatibilityComparison();
        Reload();
        IsOpen = true;
    }

    public void Close()
    {
        if (IsDisposed)
        {
            return;
        }

        CloseCompatibilityComparison();
        IsOpen = false;
    }

    public void RefreshLocalization()
    {
        if (IsDisposed)
        {
            return;
        }

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

    internal void SaveCompatibilityReport(string path)
    {
        if (IsDisposed)
        {
            return;
        }

        CreateCompatibilityReport().Save(path);
    }

    internal bool TryCompareCompatibilityReports(string baselinePath, string currentPath)
    {
        if (IsDisposed)
        {
            return false;
        }

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
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed || IsBusy || !HasItems)
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
            await _validationSession.ValidateAsync(
                Items,
                OnGalleryItemValidated,
                UpdateValidationProgress);

            if (IsDisposed)
            {
                return;
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
            if (IsDisposed)
            {
                _isBusy = false;
            }
            else
            {
                IsBusy = false;
            }
        }
    }

    private void OnGalleryItemValidated(SemiconductorRecipeGalleryItemValidationOutcome outcome)
    {
        if (IsDisposed)
        {
            return;
        }

        outcome.Item.MarkValidationCompleted(
            outcome.IsPassed,
            BuildIdentity.Current,
            BuildIdentity.Compact,
            BuildIdentity.SourceCommit,
            BuildIdentity.SourceState,
            BuildIdentity.IsExactCommit,
            outcome.FailureStep,
            outcome.Detail);
        ValidatedCount++;
        if (outcome.IsPassed)
        {
            PassedCount++;
            return;
        }

        FailedCount++;
        OnPropertyChanged(nameof(HasFirstFailure));
        if (string.IsNullOrWhiteSpace(FirstFailureRecipeName))
        {
            FirstFailureRecipeName = outcome.Item.DisplayName;
            FirstFailureStepId = outcome.FailureStep;
            FirstFailureDetail = outcome.Detail;
            SelectedItem = outcome.Item;
        }
    }

    private void UpdateValidationProgress(int validatedCount, int itemCount)
    {
        if (IsDisposed)
        {
            return;
        }

        ValidationProgressText = string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Gallery.ValidationProgressFormat"),
            validatedCount,
            itemCount);
        _saveCompatibilityReportCommand.RaiseCanExecuteChanged();
    }

    private void ResetValidationSummary()
    {
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed)
        {
            return;
        }

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
        if (IsDisposed || SelectedItem is null || IsBusy)
        {
            return false;
        }

        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var created = await _createCopy(SelectedItem, destinationPath);
            if (IsDisposed)
            {
                return false;
            }

            if (created)
            {
                Close();
            }

            return created;
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
            {
                ErrorMessage = exception.Message;
            }

            return false;
        }
        finally
        {
            if (IsDisposed)
            {
                _isBusy = false;
            }
            else
            {
                IsBusy = false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _isBusy = false;
        _isOpen = false;
        _isComparisonOpen = false;
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}
