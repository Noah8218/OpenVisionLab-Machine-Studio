using System.Windows.Input;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns Shell selection and first-use presentation state without depending on
/// a project, Runtime Engine, or visual tree.
/// </summary>
public sealed class ShellNavigationViewModel : ViewModelBase, IDisposable
{
    private readonly Func<bool> _canStartBlankLayout;
    private readonly Func<bool> _canOpenBundledSample;
    private readonly Func<Task> _openBundledSampleAsync;
    private readonly Func<bool> _canOpenLargeLayoutSample;
    private readonly Func<Task> _openLargeLayoutSampleAsync;
    private readonly Action _onBlankLayoutStarted;
    private readonly Action<Exception> _onCommandException;
    private readonly Func<bool> _resolvePendingPlacementDraft;
    private readonly Func<bool> _canOpenTestScenario;
    private readonly Func<bool> _canOpenRecipeConnections;
    private readonly Func<bool> _canOpenManualCommissioning;
    private bool _isTestScenarioOpen;
    private bool _isRecipeConnectionsOpen;
    private bool _isCompactLayout;
    private bool _isNarrowLayout;
    private bool _isStartupChoiceVisible;
    private int _selectedDocumentTabIndex;
    private int _selectedWorkspaceIndex;
    private (int Workspace, int Document, int LeftTab, bool Inspector) _resultsReturn = (0, 0, 0, true);
    private bool _isResultSettingsOpen;
    private int _selectedExecutionTabIndex;
    private int _selectedInspectionSettingsTabIndex;
    private int _selectedExperimentTabIndex;
    private int _selectedExperimentCaptureSectionIndex = 1;
    private bool _isInspectorOpen = true;
    private bool _isEvidenceExpanded;
    private bool _isSimulationDiagnosticsExpanded;
    private bool _isInspectionSettingsExpanded;
    private int _selectedEvidenceTabIndex;
    private int _selectedEquipmentEvidenceTabIndex;
    private int _selectedLeftToolTabIndex;
    private OpenVisionLanguageOption _selectedLanguageOption;
    private bool _disposed;

    public ShellNavigationViewModel(
        bool isStartupChoiceVisible,
        Func<bool> canStartBlankLayout,
        Func<bool> canOpenBundledSample,
        Func<Task> openBundledSampleAsync,
        Action onBlankLayoutStarted,
        Action<Exception> onCommandException,
        Func<bool>? canOpenLargeLayoutSample = null,
        Func<Task>? openLargeLayoutSampleAsync = null,
        Func<bool>? resolvePendingPlacementDraft = null,
        Func<bool>? canOpenTestScenario = null,
        Func<bool>? canOpenRecipeConnections = null,
        Func<bool>? canOpenManualCommissioning = null)
    {
        _canStartBlankLayout = canStartBlankLayout ?? throw new ArgumentNullException(nameof(canStartBlankLayout));
        _canOpenBundledSample = canOpenBundledSample ?? throw new ArgumentNullException(nameof(canOpenBundledSample));
        _openBundledSampleAsync = openBundledSampleAsync ?? throw new ArgumentNullException(nameof(openBundledSampleAsync));
        _canOpenLargeLayoutSample = canOpenLargeLayoutSample ?? (() => false);
        _openLargeLayoutSampleAsync = openLargeLayoutSampleAsync ?? (() => Task.CompletedTask);
        _onBlankLayoutStarted = onBlankLayoutStarted ?? throw new ArgumentNullException(nameof(onBlankLayoutStarted));
        _onCommandException = onCommandException ?? throw new ArgumentNullException(nameof(onCommandException));
        _resolvePendingPlacementDraft = resolvePendingPlacementDraft ?? (() => true);
        _canOpenTestScenario = canOpenTestScenario ?? (() => true);
        _canOpenRecipeConnections = canOpenRecipeConnections ?? _canOpenTestScenario;
        _canOpenManualCommissioning = canOpenManualCommissioning ?? (() => true);
        _isStartupChoiceVisible = isStartupChoiceVisible;
        OpenVisionLanguageService.Load();
        _selectedLanguageOption = LanguageOptions.First(option =>
            option.Language == OpenVisionLanguageService.CurrentLanguage);
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;

        StartBlankLayoutCommand = new RelayCommand(
            _ => StartBlankLayout(),
            _ => CanStartBlankLayout,
            useCommandManagerRequery: false);
        OpenBundledSampleCommand = new AsyncRelayCommand(
            async _ => await _openBundledSampleAsync(),
            _ => CanOpenBundledSample,
            _onCommandException,
            useCommandManagerRequery: false);
        OpenLargeLayoutSampleCommand = new AsyncRelayCommand(
            async _ => await _openLargeLayoutSampleAsync(),
            _ => CanOpenLargeLayoutSample,
            _onCommandException,
            useCommandManagerRequery: false);
        ToggleInspectorCommand = new RelayCommand(
            _ => IsInspectorOpen = !IsInspectorOpen,
            _ => !_disposed,
            useCommandManagerRequery: false);
        ToggleEvidenceDrawerCommand = new RelayCommand(
            _ => IsEvidenceExpanded = !IsEvidenceExpanded,
            _ => !_disposed,
            useCommandManagerRequery: false);
        SelectEquipmentEvidenceTabCommand = new RelayCommand(parameter =>
        {
            if (_disposed || !int.TryParse(parameter?.ToString(), out var index) || index is < 0 or > 3) return;
            var collapse = IsEvidenceExpanded && SelectedEquipmentEvidenceTabIndex == index;
            SelectedEquipmentEvidenceTabIndex = index;
            IsEvidenceExpanded = !collapse;
        }, _ => !_disposed, useCommandManagerRequery: false);
        OpenTestScenarioCommand = new RelayCommand(_ =>
        {
            if (CanOpenTestScenario) IsTestScenarioOpen = true;
        }, _ => CanOpenTestScenario);
        CloseTestScenarioCommand = new RelayCommand(_ =>
        {
            if (!_disposed) IsTestScenarioOpen = false;
        }, _ => !_disposed && IsTestScenarioOpen);
        OpenRecipeConnectionsCommand = new RelayCommand(_ =>
        {
            if (CanOpenRecipeConnections && _resolvePendingPlacementDraft()) IsRecipeConnectionsOpen = true;
        }, _ => CanOpenRecipeConnections);
        CloseRecipeConnectionsCommand = new RelayCommand(_ => CloseRecipeConnections(), _ => !_disposed && IsRecipeConnectionsOpen);
        CloseModalOverlayCommand = new RelayCommand(_ =>
        {
            if (IsRecipeConnectionsOpen) CloseRecipeConnections();
            else if (IsTestScenarioOpen) IsTestScenarioOpen = false;
        }, _ => !_disposed && IsModalOverlayOpen);
        OpenComponentLibraryCommand = new RelayCommand(
            _ => SelectedLeftToolTabIndex = 1,
            _ => !_disposed,
            useCommandManagerRequery: false);
        OpenEquipmentWorkspaceCommand = new RelayCommand(_ =>
        {
            if (!_disposed && !IsModalOverlayOpen) IsEquipmentWorkspace = true;
        }, _ => !_disposed && !IsModalOverlayOpen, useCommandManagerRequery: false);
        OpenResultsWorkspaceCommand = new RelayCommand(_ =>
        {
            if (!_disposed && !IsModalOverlayOpen) IsResultsWorkspace = true;
        },
            _ => !_disposed && !IsModalOverlayOpen, useCommandManagerRequery: false);
        ReturnFromResultsCommand = new RelayCommand(_ =>
        {
            if (_disposed || !IsResultsWorkspace || IsModalOverlayOpen) return;
            var previous = _resultsReturn;
            SelectWorkspace(previous.Workspace, previous.Document);
            if (IsResultsWorkspace) return;
            SelectedLeftToolTabIndex = previous.LeftTab;
            IsInspectorOpen = previous.Inspector;
        }, _ => !_disposed && IsResultsWorkspace && !IsModalOverlayOpen, useCommandManagerRequery: false);
        ReturnToResultsCommand = new RelayCommand(_ =>
        {
            if (HasResultSettingsReturn && !IsModalOverlayOpen) OpenResultsWorkspaceCommand.Execute(null);
        }, _ => HasResultSettingsReturn && !IsModalOverlayOpen, useCommandManagerRequery: false);
        OpenInspectionStatusCommand = new RelayCommand(_ =>
        {
            SelectedInspectionSettingsTabIndex = 3;
            if (IsNarrowLayout) IsInspectionSettingsExpanded = true;
        }, _ => !_disposed);
        OpenManualCommissioningCommand = new RelayCommand(
            _ =>
            {
                if (_disposed || !_canOpenManualCommissioning()) return;
                IsSimulationWorkspace = true;
                SelectedExecutionTabIndex = 0;
                if (IsSimulationWorkspace && !IsSequenceWorkspace) IsInspectorOpen = true;
            },
            _ => !_disposed && _canOpenManualCommissioning(),
            useCommandManagerRequery: false);
        OpenSequenceEditorCommand = new RelayCommand(
            _ =>
            {
                IsSimulationWorkspace = true;
                SelectedExecutionTabIndex = 1;
                if (IsSequenceWorkspace) IsInspectorOpen = true;
            },
            _ => !_disposed,
            useCommandManagerRequery: false);
    }

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<OpenVisionLanguageOption> LanguageOptions =>
        OpenVisionLanguageService.LanguageOptions;

    public ICommand StartBlankLayoutCommand { get; }

    public ICommand OpenBundledSampleCommand { get; }

    public ICommand OpenLargeLayoutSampleCommand { get; }

    public ICommand ToggleInspectorCommand { get; }

    public ICommand ToggleEvidenceDrawerCommand { get; }

    public ICommand SelectEquipmentEvidenceTabCommand { get; }

    public ICommand OpenComponentLibraryCommand { get; }

    public ICommand OpenManualCommissioningCommand { get; }

    public ICommand OpenSequenceEditorCommand { get; }

    public ICommand OpenInspectionStatusCommand { get; }

    public ICommand OpenResultsWorkspaceCommand { get; }

    public ICommand ReturnFromResultsCommand { get; }

    public ICommand ReturnToResultsCommand { get; }


    public ICommand OpenEquipmentWorkspaceCommand { get; }

    public ICommand OpenTestScenarioCommand { get; }

    public ICommand CloseTestScenarioCommand { get; }

    public ICommand OpenRecipeConnectionsCommand { get; }

    public ICommand CloseRecipeConnectionsCommand { get; }

    public ICommand CloseModalOverlayCommand { get; }

    public bool HasResultSettingsReturn => !_disposed && _isResultSettingsOpen && IsInspectionWorkspace;

    public bool IsModalOverlayOpen => IsTestScenarioOpen || IsRecipeConnectionsOpen;

    public bool IsRecipeConnectionsOpen
    {
        get => _isRecipeConnectionsOpen;
        private set
        {
            if (!SetProperty(ref _isRecipeConnectionsOpen, value)) return;
            OnPropertyChanged(nameof(IsModalOverlayOpen));
            InvalidateCommands();
        }
    }

    internal bool CanOpenRecipeConnections => !_disposed && !IsStartupChoiceVisible && !IsModalOverlayOpen && _canOpenRecipeConnections();

    internal void CloseRecipeConnections()
    {
        if (!_disposed) IsRecipeConnectionsOpen = false;
    }

    public bool IsTestScenarioOpen
    {
        get => _isTestScenarioOpen;
        private set
        {
            if (!SetProperty(ref _isTestScenarioOpen, value)) return;
            ((RelayCommand)OpenTestScenarioCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CloseTestScenarioCommand).RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsModalOverlayOpen));
            InvalidateCommands();
        }
    }

    internal bool CanOpenTestScenario => !_disposed && !IsStartupChoiceVisible && !IsModalOverlayOpen && _canOpenTestScenario();

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        set => SetProperty(ref _isCompactLayout, value);
    }

    public bool IsNarrowLayout
    {
        get => _isNarrowLayout;
        set => SetProperty(ref _isNarrowLayout, value);
    }

    public int SelectedDocumentTabIndex
    {
        get => _selectedDocumentTabIndex;
        set
        {
            if (value is < 0 or > 4) return;
            if (value == _selectedDocumentTabIndex && value switch
                {
                    0 => IsEquipmentWorkspace,
                    1 => IsInspectionWorkspace,
                    2 => IsSimulationWorkspace && SelectedExecutionTabIndex == 1,
                    3 => IsResultsWorkspace,
                    4 => IsExperimentWorkspace,
                    _ => true
                }) return;
            switch (value)
            {
                case 0:
                    SelectWorkspace(0, 0);
                    break;
                case 1:
                    SelectWorkspace(2, 1);
                    break;
                case 2:
                    SelectedWorkspaceIndex = 1;
                    if (IsSimulationWorkspace) SelectedExecutionTabIndex = 1;
                    break;
                case 3:
                    SelectWorkspace(3, 3);
                    break;
                case 4:
                    SelectWorkspace(4, 4);
                    break;
            }
        }
    }

    public int SelectedDocumentContentIndex => _selectedDocumentTabIndex switch
    {
        0 or 2 => 0,
        1 => 1,
        3 => 2,
        4 => 3,
        _ => 0
    };

    public int SelectedWorkspaceIndex
    {
        get => _selectedWorkspaceIndex;
        set
        {
            if (value is < 0 or > 4 || value == _selectedWorkspaceIndex) return;
            SelectWorkspace(value, value switch
            {
                0 => 0,
                1 => _selectedExecutionTabIndex == 1 ? 2 : 0,
                2 => 1,
                3 => 3,
                4 => 4,
                _ => 0
            });
        }
    }

    public bool IsEquipmentWorkspace
    {
        get => _selectedWorkspaceIndex == 0;
        set { if (value) SelectedWorkspaceIndex = 0; }
    }

    public bool IsSimulationWorkspace
    {
        get => _selectedWorkspaceIndex == 1;
        set { if (value) SelectedWorkspaceIndex = 1; }
    }

    public bool IsProcessWorkspace
    {
        get => IsSequenceWorkspace;
        set
        {
            if (!value) return;
            OpenSequenceEditorCommand.Execute(null);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSimulationOverviewWorkspace));
        }
    }

    public bool IsSimulationOverviewWorkspace
    {
        get => IsSimulationWorkspace && !IsSequenceWorkspace;
        set
        {
            if (!value) return;
            IsSimulationWorkspace = true;
            if (IsSimulationWorkspace) SelectedExecutionTabIndex = 0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProcessWorkspace));
        }
    }

    public bool IsEquipmentOutlineVisible => IsEquipmentWorkspace && SelectedLeftToolTabIndex == 0
        || (IsSimulationWorkspace || IsExperimentWorkspace) && SelectedLeftToolTabIndex == 3;

    public int SelectedExecutionTabIndex
    {
        get => IsSimulationWorkspace ? _selectedExecutionTabIndex : 0;
        set
        {
            if (!IsSimulationWorkspace || value is < 0 or > 1) return;
            if (_selectedExecutionTabIndex == value) return;
            if (!_resolvePendingPlacementDraft()) { OnPropertyChanged(); return; }
            if (!SetProperty(ref _selectedExecutionTabIndex, value)) return;
            OnPropertyChanged(nameof(IsSequenceWorkspace));
            OnPropertyChanged(nameof(IsProcessWorkspace));
            OnPropertyChanged(nameof(IsSimulationOverviewWorkspace));
            OnPropertyChanged(nameof(IsDockedInspectorOpen));
            if (value == 1) IsInspectorOpen = true;
            var documentTabIndex = value == 1 ? 2 : 0;
            if (_selectedDocumentTabIndex != documentTabIndex)
            {
                _selectedDocumentTabIndex = documentTabIndex;
                OnPropertyChanged(nameof(SelectedDocumentTabIndex));
                OnPropertyChanged(nameof(SelectedDocumentContentIndex));
            }
        }
    }

    public bool IsInspectionWorkspace
    {
        get => _selectedWorkspaceIndex == 2;
        set { if (value) SelectedWorkspaceIndex = 2; }
    }

    public int SelectedInspectionSettingsTabIndex
    {
        get => _selectedInspectionSettingsTabIndex;
        set
        {
            if (value is < 0 or > 3) return;
            SetProperty(ref _selectedInspectionSettingsTabIndex, value);
        }
    }

    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set
        {
            if (!value && _isInspectorOpen && !_resolvePendingPlacementDraft())
            {
                OnPropertyChanged();
                return;
            }
            if (SetProperty(ref _isInspectorOpen, value))
            {
                OnPropertyChanged(nameof(IsEquipmentInspectorOpen));
                OnPropertyChanged(nameof(IsDockedInspectorOpen));
            }
        }
    }

    public bool IsEquipmentInspectorOpen => IsEquipmentWorkspace && IsInspectorOpen;
    public bool IsSequenceWorkspace => IsSimulationWorkspace && SelectedExecutionTabIndex == 1;
    public bool IsDockedInspectorOpen => (IsEquipmentWorkspace || IsSequenceWorkspace) && IsInspectorOpen;

    public bool IsEvidenceExpanded
    {
        get => _isEvidenceExpanded;
        set
        {
            if (!SetProperty(ref _isEvidenceExpanded, value)) return;
            RaiseEquipmentEvidenceSelectionChanged();
        }
    }

    public bool IsSimulationDiagnosticsExpanded
    {
        get => _isSimulationDiagnosticsExpanded;
        set => SetProperty(ref _isSimulationDiagnosticsExpanded, value);
    }

    public bool IsInspectionSettingsExpanded
    {
        get => _isInspectionSettingsExpanded;
        set => SetProperty(ref _isInspectionSettingsExpanded, value);
    }

    public int SelectedEvidenceTabIndex
    {
        get => _selectedEvidenceTabIndex;
        set
        {
            if (value is < 0 or > 3) return;
            SetProperty(ref _selectedEvidenceTabIndex, value);
        }
    }

    public int SelectedEquipmentEvidenceTabIndex
    {
        get => _selectedEquipmentEvidenceTabIndex;
        set
        {
            if (value is < 0 or > 3 || !SetProperty(ref _selectedEquipmentEvidenceTabIndex, value)) return;
            RaiseEquipmentEvidenceSelectionChanged();
        }
    }

    public bool IsEquipmentEventsSelected => IsEvidenceExpanded && SelectedEquipmentEvidenceTabIndex == 0;
    public bool IsEquipmentRequestsSelected => IsEvidenceExpanded && SelectedEquipmentEvidenceTabIndex == 1;
    public bool IsEquipmentGenerationSelected => IsEvidenceExpanded && SelectedEquipmentEvidenceTabIndex == 2;
    public bool IsEquipmentAlarmsSelected => IsEvidenceExpanded && SelectedEquipmentEvidenceTabIndex == 3;

    public bool IsExperimentWorkspace
    {
        get => _selectedWorkspaceIndex == 4;
        set { if (value) SelectedWorkspaceIndex = 4; }
    }

    public int SelectedExperimentTabIndex
    {
        get => _selectedExperimentTabIndex;
        set { if (value is >= 0 and <= 2) SetProperty(ref _selectedExperimentTabIndex, value); }
    }

    public int SelectedExperimentCaptureSectionIndex
    {
        get => _selectedExperimentCaptureSectionIndex;
        set { if (value is >= 0 and <= 2) SetProperty(ref _selectedExperimentCaptureSectionIndex, value); }
    }

    public bool IsResultsWorkspace
    {
        get => _selectedWorkspaceIndex == 3;
        set { if (value) SelectedWorkspaceIndex = 3; }
    }

    public int SelectedLeftToolTabIndex
    {
        get => _selectedLeftToolTabIndex;
        set
        {
            if (!SetProperty(ref _selectedLeftToolTabIndex, value)) return;
            OnPropertyChanged(nameof(IsEquipmentOutlineVisible));
        }
    }

    public bool IsStartupChoiceVisible => _isStartupChoiceVisible;

    public OpenVisionLanguageOption SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedLanguageOption, value))
            {
                return;
            }

            _selectedLanguageOption = value;
            OnPropertyChanged();
            OpenVisionLanguageService.SetLanguage(value.Language);
        }
    }

    internal bool CanStartBlankLayout => !_disposed
        && IsStartupChoiceVisible
        && _canStartBlankLayout();

    internal bool CanOpenBundledSample => !_disposed
        && IsStartupChoiceVisible
        && _canOpenBundledSample();

    internal bool CanOpenLargeLayoutSample => !_disposed
        && IsStartupChoiceVisible
        && _canOpenLargeLayoutSample();

    public void HideStartupChoice()
    {
        if (_disposed || !SetProperty(ref _isStartupChoiceVisible, false, nameof(IsStartupChoiceVisible)))
        {
            return;
        }

        InvalidateCommands();
    }

    public void InvalidateCommands()
    {
        ((RelayCommand)StartBlankLayoutCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)OpenBundledSampleCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)OpenLargeLayoutSampleCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ToggleInspectorCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ToggleEvidenceDrawerCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenResultsWorkspaceCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ReturnFromResultsCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ReturnToResultsCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenEquipmentWorkspaceCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SelectEquipmentEvidenceTabCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenComponentLibraryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenManualCommissioningCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenSequenceEditorCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenTestScenarioCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CloseTestScenarioCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenRecipeConnectionsCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CloseRecipeConnectionsCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CloseModalOverlayCommand).RaiseCanExecuteChanged();
    }

    internal void ClearResultSettingsReturn()
    {
        if (!_isResultSettingsOpen) return;
        _isResultSettingsOpen = false;
        OnPropertyChanged(nameof(HasResultSettingsReturn));
        InvalidateCommands();
    }

    internal bool OpenResultCameraSettings()
    {
        if (_disposed || !IsResultsWorkspace || IsModalOverlayOpen) return false;
        IsInspectionWorkspace = true;
        if (!IsInspectionWorkspace) return false;
        _isResultSettingsOpen = true;
        SelectedInspectionSettingsTabIndex = 0;
        IsInspectionSettingsExpanded = true;
        IsInspectorOpen = true;
        OnPropertyChanged(nameof(HasResultSettingsReturn));
        InvalidateCommands();
        return true;
    }

    internal void EnterEquipmentAuthoring()
    {
        if (_disposed) return;
        HideStartupChoice();
        IsEquipmentWorkspace = true;
        SelectedLeftToolTabIndex = 0;
        IsInspectorOpen = true;
        IsEvidenceExpanded = false;
    }

    private void StartBlankLayout()
    {
        if (!CanStartBlankLayout)
        {
            return;
        }

        EnterEquipmentAuthoring();
        _onBlankLayoutStarted();
        InvalidateCommands();
    }

    private void SelectWorkspace(int workspaceIndex, int documentIndex)
    {
        var previousExecutionTabIndex = SelectedExecutionTabIndex;
        var wasExperimentWorkspace = IsExperimentWorkspace;
        var workspaceChanged = _selectedWorkspaceIndex != workspaceIndex;
        var documentChanged = _selectedDocumentTabIndex != documentIndex;
        if ((workspaceChanged || documentChanged) && !_resolvePendingPlacementDraft())
        {
            OnPropertyChanged(string.Empty);
            return;
        }
        if (workspaceChanged && workspaceIndex == 3 && !_isResultSettingsOpen)
            _resultsReturn = (_selectedWorkspaceIndex, _selectedDocumentTabIndex, _selectedLeftToolTabIndex, IsInspectorOpen);
        if (workspaceChanged && workspaceIndex != 2 && _isResultSettingsOpen)
        {
            _isResultSettingsOpen = false;
            OnPropertyChanged(nameof(HasResultSettingsReturn));
        }
        _selectedWorkspaceIndex = workspaceIndex;
        _selectedDocumentTabIndex = documentIndex;
        if (workspaceChanged && workspaceIndex is 1 or 4)
        {
            SelectedLeftToolTabIndex = 3;
        }
        else if (workspaceChanged && _selectedLeftToolTabIndex == 3)
        {
            SelectedLeftToolTabIndex = 0;
        }
        if (workspaceIndex == 0 && _selectedLeftToolTabIndex == 2)
        {
            SelectedLeftToolTabIndex = 0;
        }
        if (workspaceChanged)
        {
            IsInspectorOpen = workspaceIndex == 0;
        }
        if (documentChanged)
        {
            OnPropertyChanged(nameof(SelectedDocumentTabIndex));
            OnPropertyChanged(nameof(SelectedDocumentContentIndex));
        }
        if (!workspaceChanged) return;
        OnPropertyChanged(nameof(SelectedWorkspaceIndex));
        OnPropertyChanged(nameof(IsEquipmentWorkspace));
        OnPropertyChanged(nameof(IsEquipmentInspectorOpen));
        OnPropertyChanged(nameof(IsSequenceWorkspace));
        OnPropertyChanged(nameof(IsProcessWorkspace));
        OnPropertyChanged(nameof(IsSimulationOverviewWorkspace));
        OnPropertyChanged(nameof(IsDockedInspectorOpen));
        OnPropertyChanged(nameof(IsSimulationWorkspace));
        OnPropertyChanged(nameof(IsInspectionWorkspace));
        OnPropertyChanged(nameof(IsResultsWorkspace));
        if (wasExperimentWorkspace != IsExperimentWorkspace) OnPropertyChanged(nameof(IsExperimentWorkspace));
        OnPropertyChanged(nameof(IsEquipmentOutlineVisible));
        if (previousExecutionTabIndex != SelectedExecutionTabIndex)
            OnPropertyChanged(nameof(SelectedExecutionTabIndex));
        InvalidateCommands();
    }

    private void RaiseEquipmentEvidenceSelectionChanged()
    {
        OnPropertyChanged(nameof(IsEquipmentEventsSelected));
        OnPropertyChanged(nameof(IsEquipmentRequestsSelected));
        OnPropertyChanged(nameof(IsEquipmentGenerationSelected));
        OnPropertyChanged(nameof(IsEquipmentAlarmsSelected));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var option = LanguageOptions.FirstOrDefault(item =>
            item.Language == OpenVisionLanguageService.CurrentLanguage);
        if (option is not null && !ReferenceEquals(_selectedLanguageOption, option))
        {
            _selectedLanguageOption = option;
            OnPropertyChanged(nameof(SelectedLanguageOption));
        }

        LanguageChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        IsTestScenarioOpen = false;
        IsRecipeConnectionsOpen = false;
        _disposed = true;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        LanguageChanged = null;
        InvalidateCommands();
    }
}
