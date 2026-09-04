using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Layout;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns the read-only scene playback state for a completed recipe dry-run.
/// Shell concerns such as the live runtime, layout host, and document tabs are
/// reached only through explicit callbacks supplied by <see cref="MainViewModel"/>.
/// </summary>
public sealed class RecipeDryRunPlaybackViewModel : ViewModelBase
{
    private readonly Func<bool> _isDesignMode;
    private readonly Action<bool> _setLayoutEditable;
    private readonly Action<int> _selectDocumentTab;
    private readonly Action<RecipeDryRunStepPresentation> _selectDryRunStep;
    private readonly Action<string> _setStatus;
    private readonly RelayCommand _previousStepCommand;
    private readonly RelayCommand _nextStepCommand;
    private readonly RelayCommand _exitCommand;
    private RecipeDryRunStepPresentation[] _steps = [];
    private int _index = -1;
    private bool _isActive;

    public RecipeDryRunPlaybackViewModel(
        Func<bool> isDesignMode,
        Action<bool> setLayoutEditable,
        Action<int> selectDocumentTab,
        Action<RecipeDryRunStepPresentation> selectDryRunStep,
        Action<string> setStatus)
    {
        _isDesignMode = isDesignMode ?? throw new ArgumentNullException(nameof(isDesignMode));
        _setLayoutEditable = setLayoutEditable ?? throw new ArgumentNullException(nameof(setLayoutEditable));
        _selectDocumentTab = selectDocumentTab ?? throw new ArgumentNullException(nameof(selectDocumentTab));
        _selectDryRunStep = selectDryRunStep ?? throw new ArgumentNullException(nameof(selectDryRunStep));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _previousStepCommand = new RelayCommand(
            _ => Move(-1),
            _ => _isActive && _index > 0,
            useCommandManagerRequery: false);
        _nextStepCommand = new RelayCommand(
            _ => Move(1),
            _ => _isActive && _index + 1 < _steps.Length,
            useCommandManagerRequery: false);
        _exitCommand = new RelayCommand(
            _ => Exit(),
            _ => _isActive,
            useCommandManagerRequery: false);
    }

    public SceneSnapshotStore PlaybackSnapshots { get; } = new();

    public ICommand PreviousStepCommand => _previousStepCommand;
    public ICommand NextStepCommand => _nextStepCommand;
    public ICommand ExitCommand => _exitCommand;

    public bool IsActive => _isActive;

    public RecipeDryRunStepPresentation? CurrentStep =>
        _index >= 0 && _index < _steps.Length ? _steps[_index] : null;

    public string TitleText => CurrentStep is null
        ? string.Empty
        : Format(
            "Connections.DryRunPlaybackTitleFormat",
            _index + 1,
            _steps.Length,
            CurrentStep.Name);

    public string DetailText => CurrentStep is null
        ? string.Empty
        : Format("Connections.DryRunPlaybackDetailFormat", CurrentStep.TickText);

    public bool HasCheckpoint => CurrentStep?.HasCheckpoint == true;
    public bool HasMismatch => CurrentStep?.HasCheckpointMismatch == true;
    public string CheckpointText => CurrentStep?.CheckpointText ?? string.Empty;

    private LoadLockSnapshot? LoadLock => CurrentStep?.BoundarySnapshot.LoadLocks.FirstOrDefault();
    public bool HasLoadLock => LoadLock is not null;
    public bool IsLoadLockFault => LoadLock?.State == LoadLockState.InterlockFault;
    public string LoadLockText => LoadLock is { } loadLock
        ? RecipeDryRunViewModel.FormatLoadLockStatus(loadLock)
        : string.Empty;

    private WaferHandlerSnapshot? WaferHandler =>
        CurrentStep?.BoundarySnapshot.WaferHandlers.FirstOrDefault();
    public bool HasWaferHandler => WaferHandler is not null;
    public bool IsWaferHandlerFault =>
        WaferHandler?.State == WaferHandlerOwnershipState.InterlockFault;
    public string WaferHandlerText => WaferHandler is { } handler
        ? RecipeDryRunViewModel.FormatWaferHandlerStatus(handler)
        : string.Empty;

    private InspectionSortRouterSnapshot? InspectionSorter =>
        CurrentStep?.BoundarySnapshot.InspectionSortRouters.FirstOrDefault();
    public bool HasInspectionSorter => InspectionSorter is not null;
    public bool IsInspectionSorterFault =>
        InspectionSorter?.State == InspectionSortRouteState.InterlockFault;
    public string InspectionSorterText => InspectionSorter is { } sorter
        ? RecipeDryRunViewModel.FormatInspectionSorterStatus(sorter)
        : string.Empty;

    private InspectionHandoffSnapshot? InspectionHandoff =>
        CurrentStep?.BoundarySnapshot.InspectionHandoffs.FirstOrDefault();
    public bool HasInspectionHandoff => InspectionHandoff is not null;
    public bool IsInspectionHandoffFault =>
        InspectionHandoff?.State == InspectionHandoffState.InterlockFault;
    public string InspectionHandoffText => InspectionHandoff is { } handoff
        ? RecipeDryRunViewModel.FormatInspectionHandoffStatus(handoff)
        : string.Empty;

    private OhtHandoffSnapshot? OhtHandoff =>
        CurrentStep?.BoundarySnapshot.OhtHandoffs.FirstOrDefault();
    public bool HasOhtHandoff => OhtHandoff is not null;
    public bool IsOhtHandoffFault =>
        OhtHandoff?.State == OhtHandoffOwnershipState.InterlockFault;
    public string OhtHandoffText => OhtHandoff is { } handoff
        ? RecipeDryRunViewModel.FormatOhtHandoffStatus(handoff)
        : string.Empty;

    private PrealignerSnapshot? Prealigner =>
        CurrentStep?.BoundarySnapshot.Prealigners.FirstOrDefault();
    public bool HasPrealigner => Prealigner is not null;
    public bool IsPrealignerFault => Prealigner?.State == PrealignerState.InterlockFault;
    public string PrealignerText => Prealigner is { } prealigner
        ? RecipeDryRunViewModel.FormatPrealignerStatus(prealigner)
        : string.Empty;

    public void Show(RecipeDryRunStepPresentation step, IReadOnlyList<RecipeDryRunStepPresentation> steps)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(steps);

        var playbackSteps = steps.ToArray();
        var index = Array.IndexOf(playbackSteps, step);
        if (index < 0)
        {
            return;
        }

        _steps = playbackSteps;
        _index = index;
        PlaybackSnapshots.Publish(step.BoundarySnapshot);
        _isActive = true;
        _setLayoutEditable(false);
        _selectDocumentTab(0);
        RaisePlaybackChanged();
        _setStatus(OpenVisionLanguageService.T("Connections.DryRunPlaybackStatus"));
    }

    public void Exit()
    {
        if (!_isActive)
        {
            return;
        }

        _isActive = false;
        _steps = [];
        _index = -1;
        _setLayoutEditable(_isDesignMode());
        RaisePlaybackChanged();
    }

    internal void InvalidateCommands()
    {
        _previousStepCommand.RaiseCanExecuteChanged();
        _nextStepCommand.RaiseCanExecuteChanged();
        _exitCommand.RaiseCanExecuteChanged();
    }

    private void Move(int offset)
    {
        var index = _index + offset;
        if (!_isActive || index < 0 || index >= _steps.Length)
        {
            return;
        }

        _index = index;
        var step = _steps[index];
        _selectDryRunStep(step);
        PlaybackSnapshots.Publish(step.BoundarySnapshot);
        RaisePlaybackChanged();
    }

    private void RaisePlaybackChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(HasCheckpoint));
        OnPropertyChanged(nameof(HasMismatch));
        OnPropertyChanged(nameof(CheckpointText));
        OnPropertyChanged(nameof(HasLoadLock));
        OnPropertyChanged(nameof(IsLoadLockFault));
        OnPropertyChanged(nameof(LoadLockText));
        OnPropertyChanged(nameof(HasWaferHandler));
        OnPropertyChanged(nameof(IsWaferHandlerFault));
        OnPropertyChanged(nameof(WaferHandlerText));
        OnPropertyChanged(nameof(HasInspectionSorter));
        OnPropertyChanged(nameof(IsInspectionSorterFault));
        OnPropertyChanged(nameof(InspectionSorterText));
        OnPropertyChanged(nameof(HasInspectionHandoff));
        OnPropertyChanged(nameof(IsInspectionHandoffFault));
        OnPropertyChanged(nameof(InspectionHandoffText));
        OnPropertyChanged(nameof(HasOhtHandoff));
        OnPropertyChanged(nameof(IsOhtHandoffFault));
        OnPropertyChanged(nameof(OhtHandoffText));
        OnPropertyChanged(nameof(HasPrealigner));
        OnPropertyChanged(nameof(IsPrealignerFault));
        OnPropertyChanged(nameof(PrealignerText));
        InvalidateCommands();
    }

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
}
