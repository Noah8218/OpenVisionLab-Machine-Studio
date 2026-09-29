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
    private bool _isCompactLayout;
    private bool _isNarrowLayout;
    private bool _isStartupChoiceVisible;
    private int _selectedDocumentTabIndex;
    private int _selectedWorkspaceIndex;
    private int _selectedExecutionTabIndex;
    private int _selectedInspectionTabIndex;
    private bool _isInspectorOpen;
    private bool _isEvidenceExpanded;
    private bool _isInspectionSettingsExpanded;
    private int _selectedEvidenceTabIndex;
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
        Func<bool>? resolvePendingPlacementDraft = null)
    {
        _canStartBlankLayout = canStartBlankLayout ?? throw new ArgumentNullException(nameof(canStartBlankLayout));
        _canOpenBundledSample = canOpenBundledSample ?? throw new ArgumentNullException(nameof(canOpenBundledSample));
        _openBundledSampleAsync = openBundledSampleAsync ?? throw new ArgumentNullException(nameof(openBundledSampleAsync));
        _canOpenLargeLayoutSample = canOpenLargeLayoutSample ?? (() => false);
        _openLargeLayoutSampleAsync = openLargeLayoutSampleAsync ?? (() => Task.CompletedTask);
        _onBlankLayoutStarted = onBlankLayoutStarted ?? throw new ArgumentNullException(nameof(onBlankLayoutStarted));
        _onCommandException = onCommandException ?? throw new ArgumentNullException(nameof(onCommandException));
        _resolvePendingPlacementDraft = resolvePendingPlacementDraft ?? (() => true);
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
        OpenComponentLibraryCommand = new RelayCommand(
            _ => SelectedLeftToolTabIndex = 1,
            _ => !_disposed,
            useCommandManagerRequery: false);
        OpenSequenceEditorCommand = new RelayCommand(
            _ =>
            {
                IsSimulationWorkspace = true;
                SelectedExecutionTabIndex = 1;
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

    public ICommand OpenComponentLibraryCommand { get; }

    public ICommand OpenSequenceEditorCommand { get; }

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
            if (value is < 0 or > 3) return;
            if (value == _selectedDocumentTabIndex && value switch
                {
                    0 => IsEquipmentWorkspace,
                    1 => IsInspectionWorkspace,
                    2 => IsSimulationWorkspace && SelectedExecutionTabIndex == 1,
                    3 => IsResultsWorkspace,
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
            }
        }
    }

    public int SelectedDocumentContentIndex => _selectedDocumentTabIndex switch
    {
        0 or 2 => 0,
        1 => 1,
        3 => 2,
        _ => 0
    };

    public int SelectedWorkspaceIndex
    {
        get => _selectedWorkspaceIndex;
        set
        {
            if (value is < 0 or > 3 || value == _selectedWorkspaceIndex) return;
            SelectWorkspace(value, value switch
            {
                0 => 0,
                1 => _selectedExecutionTabIndex == 1 ? 2 : 0,
                2 => 1,
                3 => 3,
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

    public bool IsEquipmentOutlineVisible => IsEquipmentWorkspace && SelectedLeftToolTabIndex == 0
        || IsSimulationWorkspace && SelectedLeftToolTabIndex == 3;

    public int SelectedExecutionTabIndex
    {
        get => IsSimulationWorkspace ? _selectedExecutionTabIndex : 0;
        set
        {
            if (!IsSimulationWorkspace || value is < 0 or > 2) return;
            if (!SetProperty(ref _selectedExecutionTabIndex, value)) return;
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

    public int SelectedInspectionTabIndex
    {
        get => _selectedInspectionTabIndex;
        set
        {
            if (value is < 0 or > 1) return;
            SetProperty(ref _selectedInspectionTabIndex, value);
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
            SetProperty(ref _isInspectorOpen, value);
        }
    }

    public bool IsEvidenceExpanded
    {
        get => _isEvidenceExpanded;
        set => SetProperty(ref _isEvidenceExpanded, value);
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
        ((RelayCommand)OpenComponentLibraryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)OpenSequenceEditorCommand).RaiseCanExecuteChanged();
    }

    private void StartBlankLayout()
    {
        if (!CanStartBlankLayout)
        {
            return;
        }

        _isStartupChoiceVisible = false;
        OnPropertyChanged(nameof(IsStartupChoiceVisible));
        SelectedLeftToolTabIndex = 1;
        _onBlankLayoutStarted();
        InvalidateCommands();
    }

    private void SelectWorkspace(int workspaceIndex, int documentIndex)
    {
        var previousExecutionTabIndex = SelectedExecutionTabIndex;
        var workspaceChanged = _selectedWorkspaceIndex != workspaceIndex;
        var documentChanged = _selectedDocumentTabIndex != documentIndex;
        if ((workspaceChanged || documentChanged) && !_resolvePendingPlacementDraft())
        {
            OnPropertyChanged(string.Empty);
            return;
        }
        _selectedWorkspaceIndex = workspaceIndex;
        _selectedDocumentTabIndex = documentIndex;
        if (workspaceChanged && workspaceIndex == 1)
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
        if (workspaceChanged && IsInspectorOpen)
        {
            IsInspectorOpen = false;
        }
        if (documentChanged)
        {
            OnPropertyChanged(nameof(SelectedDocumentTabIndex));
            OnPropertyChanged(nameof(SelectedDocumentContentIndex));
        }
        if (!workspaceChanged) return;
        OnPropertyChanged(nameof(SelectedWorkspaceIndex));
        OnPropertyChanged(nameof(IsEquipmentWorkspace));
        OnPropertyChanged(nameof(IsSimulationWorkspace));
        OnPropertyChanged(nameof(IsInspectionWorkspace));
        OnPropertyChanged(nameof(IsResultsWorkspace));
        OnPropertyChanged(nameof(IsEquipmentOutlineVisible));
        if (previousExecutionTabIndex != SelectedExecutionTabIndex)
            OnPropertyChanged(nameof(SelectedExecutionTabIndex));
        InvalidateCommands();
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

        _disposed = true;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        LanguageChanged = null;
        InvalidateCommands();
    }
}
