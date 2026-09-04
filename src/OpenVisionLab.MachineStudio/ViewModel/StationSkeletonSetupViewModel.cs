using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record SemiconductorStationSkeletonItemPresentation(
    string RoleText,
    string DetailText,
    bool IsProposed,
    bool IsAlreadyConfigured,
    bool IsUnavailable);

public sealed class StationSkeletonSetupViewModel : ViewModelBase
{
    private readonly Func<SemiconductorStationSetupDefinition, int> _applyStationSkeleton;
    private readonly Action _clearCompetingPreviews;
    private readonly SemiconductorStationSkeletonTemplate _stationSkeletonTemplate = new();
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private SemiconductorStationSkeletonPreview? _stationSkeletonPreview;
    private string _stationName = SemiconductorStationSetupDefinition.DefaultStationName;
    private string _waferType = SemiconductorStationSetupDefinition.DefaultWaferType;
    private string _axisTravelText = string.Empty;
    private string _transportSpeedText = string.Empty;
    private string _entrySensorPositionText = string.Empty;
    private string _processSensorPositionText = string.Empty;
    private string _cylinderTravelTimeText = string.Empty;
    private bool _stationSetupWasInvalid;

    public StationSkeletonSetupViewModel(
        Func<SemiconductorStationSetupDefinition, int> applyStationSkeleton,
        Action clearCompetingPreviews)
    {
        _applyStationSkeleton = applyStationSkeleton;
        _clearCompetingPreviews = clearCompetingPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => IsEditable);
        _applyCommand = new RelayCommand(
            _ => Apply(),
            ignored => IsEditable
                       && _stationSkeletonPreview is { UnavailableCount: 0 }
                       && TryCreateStationSetup(out _));
        _cancelCommand = new RelayCommand(_ => ClearPreview(), _ => IsStationSkeletonPreviewVisible);
        _resetCommand = new RelayCommand(
            _ => Reset(),
            _ => IsEditable && IsStationSkeletonPreviewVisible);
    }

    public ObservableCollection<SemiconductorStationSkeletonItemPresentation> StationSkeletonItems { get; } = new();

    public ICommand PreviewStationSkeletonCommand => _previewCommand;
    public ICommand ApplyStationSkeletonCommand => _applyCommand;
    public ICommand CancelStationSkeletonCommand => _cancelCommand;
    public ICommand ResetStationSetupCommand => _resetCommand;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value))
            {
                return;
            }

            RaiseCommandStates();
        }
    }

    public bool IsStationSkeletonPreviewVisible => _stationSkeletonPreview is not null;
    public int StationSkeletonProposedCount => _stationSkeletonPreview?.ProposedCount ?? 0;
    public string StationSkeletonSummaryText => Format(
        "Connections.StationSkeletonSummaryFormat",
        _stationSkeletonPreview?.ProposedCount ?? 0,
        _stationSkeletonPreview?.ExistingCount ?? 0,
        _stationSkeletonPreview?.UnavailableCount ?? 0);
    public string StationSkeletonApplyText => Format(
        StationSkeletonProposedCount > 0
            ? "Connections.StationSetupApplyWithRolesFormat"
            : "Connections.StationSetupApplyOnly",
        StationSkeletonProposedCount);

    public string StationName
    {
        get => _stationName;
        set => SetStationSetupText(ref _stationName, value, nameof(StationName));
    }

    public string WaferType
    {
        get => _waferType;
        set => SetStationSetupText(ref _waferType, value, nameof(WaferType));
    }

    public string AxisTravelText
    {
        get => _axisTravelText;
        set => SetStationSetupText(ref _axisTravelText, value, nameof(AxisTravelText));
    }

    public string TransportSpeedText
    {
        get => _transportSpeedText;
        set => SetStationSetupText(ref _transportSpeedText, value, nameof(TransportSpeedText));
    }

    public string EntrySensorPositionText
    {
        get => _entrySensorPositionText;
        set => SetStationSetupText(ref _entrySensorPositionText, value, nameof(EntrySensorPositionText));
    }

    public string ProcessSensorPositionText
    {
        get => _processSensorPositionText;
        set => SetStationSetupText(ref _processSensorPositionText, value, nameof(ProcessSensorPositionText));
    }

    public string CylinderTravelTimeText
    {
        get => _cylinderTravelTimeText;
        set => SetStationSetupText(ref _cylinderTravelTimeText, value, nameof(CylinderTravelTimeText));
    }

    public bool IsStationNameValid => !string.IsNullOrWhiteSpace(StationName);
    public bool IsWaferTypeValid => !string.IsNullOrWhiteSpace(WaferType);
    public bool IsAxisTravelValid => TryPositiveDouble(AxisTravelText, out _);
    public bool IsTransportSpeedValid => TryPositiveDouble(TransportSpeedText, out _);
    public bool IsEntrySensorPositionValid => TryFiniteDouble(EntrySensorPositionText, out var entry)
        && (!TryFiniteDouble(ProcessSensorPositionText, out var process) || entry < process);
    public bool IsProcessSensorPositionValid => TryFiniteDouble(ProcessSensorPositionText, out var process)
        && (!TryFiniteDouble(EntrySensorPositionText, out var entry) || entry < process);
    public bool IsCylinderTravelTimeValid => int.TryParse(
        CylinderTravelTimeText,
        NumberStyles.Integer,
        CultureInfo.CurrentCulture,
        out var timing) && timing > 0;
    public bool HasStationSetupValidationError => !TryCreateStationSetup(out _);
    public string StationSetupValidationText => HasStationSetupValidationError
        ? OpenVisionLanguageService.T("Connections.StationSetupValidationError")
        : _stationSetupWasInvalid
            ? OpenVisionLanguageService.T("Connections.StationSetupInvalidRestored")
            : OpenVisionLanguageService.T("Connections.StationSetupValidationReady");

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearPreview();
        RaiseCommandStates();
    }

    public void ClearPreviewForCompetingSetup() => ClearPreview();

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        var draft = IsStationSkeletonPreviewVisible
            ? new[]
            {
                StationName,
                WaferType,
                AxisTravelText,
                TransportSpeedText,
                EntrySensorPositionText,
                ProcessSensorPositionText,
                CylinderTravelTimeText
            }
            : null;

        reloadWorkbench();
        if (draft is null)
        {
            return;
        }

        Preview();
        StationName = draft[0];
        WaferType = draft[1];
        AxisTravelText = draft[2];
        TransportSpeedText = draft[3];
        EntrySensorPositionText = draft[4];
        ProcessSensorPositionText = draft[5];
        CylinderTravelTimeText = draft[6];
    }

    internal static SemiconductorStationSkeletonItemPresentation CreateStationSkeletonItem(
        SemiconductorStationSkeletonEntry entry)
    {
        var roleText = OpenVisionLanguageService.T($"Connections.StationSkeletonRole.{entry.Role}");
        var target = entry.Role == SemiconductorStationSkeletonRole.RequiredIo
            ? Format("Connections.StationSkeletonIoFormat", entry.ExistingCount + entry.AddedCount)
            : entry.TargetId ?? "—";
        var detailText = entry.Status switch
        {
            SemiconductorStationSkeletonStatus.Proposed => Format(
                "Connections.StationSkeletonProposedFormat",
                target),
            SemiconductorStationSkeletonStatus.Existing => Format(
                "Connections.StationSkeletonExistingFormat",
                target),
            _ when entry.UnavailableReason
                == SemiconductorStationSkeletonUnavailableReason.ActiveLayoutConflict =>
                OpenVisionLanguageService.T("Connections.StationSkeletonLayoutConflict"),
            _ when entry.UnavailableReason
                == SemiconductorStationSkeletonUnavailableReason.AutomaticSequenceConflict =>
                OpenVisionLanguageService.T("Connections.StationSkeletonSequenceConflict"),
            _ => OpenVisionLanguageService.T("Connections.StationSkeletonUnavailable")
        };
        return new SemiconductorStationSkeletonItemPresentation(
            roleText,
            detailText,
            entry.Status == SemiconductorStationSkeletonStatus.Proposed,
            entry.Status == SemiconductorStationSkeletonStatus.Existing,
            entry.Status == SemiconductorStationSkeletonStatus.Unavailable);
    }

    private void Preview()
    {
        if (_project is null)
        {
            return;
        }

        _clearCompetingPreviews();
        var setup = _stationSkeletonTemplate.ResolveSetup(_project);
        _stationSetupWasInvalid = _project.SemiconductorStationSetup is not null
            && !SemiconductorStationSkeletonTemplate.IsValidSetup(_project.SemiconductorStationSetup);
        ApplyStationSetupText(setup);
        _stationSkeletonPreview = _stationSkeletonTemplate.Preview(_project, setup);
        StationSkeletonItems.Clear();
        foreach (var entry in _stationSkeletonPreview.Entries)
        {
            StationSkeletonItems.Add(CreateStationSkeletonItem(entry));
        }
        RaiseStationSkeletonChanged();
    }

    private void Apply()
    {
        if (_project is null
            || _stationSkeletonPreview is not { UnavailableCount: 0 }
            || !TryCreateStationSetup(out var setup))
        {
            return;
        }

        if (_applyStationSkeleton(setup) <= 0)
        {
            Preview();
        }
    }

    private void Reset()
    {
        _stationSetupWasInvalid = false;
        ApplyStationSetupText(new SemiconductorStationSetupDefinition());
    }

    private void ApplyStationSetupText(SemiconductorStationSetupDefinition setup)
    {
        _stationName = setup.StationName;
        _waferType = setup.WaferType;
        _axisTravelText = FormatNumber(setup.AxisTravel);
        _transportSpeedText = FormatNumber(setup.TransportSpeed);
        _entrySensorPositionText = FormatNumber(setup.EntrySensorPosition);
        _processSensorPositionText = FormatNumber(setup.ProcessSensorPosition);
        _cylinderTravelTimeText = setup.CylinderTravelTimeMilliseconds.ToString(CultureInfo.CurrentCulture);
        RaiseStationSetupChanged();
    }

    private void SetStationSetupText(ref string field, string value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        _stationSetupWasInvalid = false;
        RaiseStationSetupValidationChanged();
    }

    private bool TryCreateStationSetup(out SemiconductorStationSetupDefinition setup)
    {
        var axisValid = TryPositiveDouble(AxisTravelText, out var axisTravel);
        var speedValid = TryPositiveDouble(TransportSpeedText, out var transportSpeed);
        var entryValid = TryFiniteDouble(EntrySensorPositionText, out var entryPosition);
        var processValid = TryFiniteDouble(ProcessSensorPositionText, out var processPosition);
        var timingValid = int.TryParse(
            CylinderTravelTimeText,
            NumberStyles.Integer,
            CultureInfo.CurrentCulture,
            out var cylinderTiming) && cylinderTiming > 0;
        setup = new SemiconductorStationSetupDefinition
        {
            StationName = StationName.Trim(),
            WaferType = WaferType.Trim(),
            AxisTravel = axisTravel,
            TransportSpeed = transportSpeed,
            EntrySensorPosition = entryPosition,
            ProcessSensorPosition = processPosition,
            CylinderTravelTimeMilliseconds = cylinderTiming
        };
        return IsStationNameValid
            && IsWaferTypeValid
            && axisValid
            && speedValid
            && entryValid
            && processValid
            && timingValid
            && SemiconductorStationSkeletonTemplate.IsValidSetup(setup);
    }

    private void ClearPreview()
    {
        _stationSkeletonPreview = null;
        StationSkeletonItems.Clear();
        RaiseStationSkeletonChanged();
    }

    private void RaiseStationSkeletonChanged()
    {
        OnPropertyChanged(nameof(IsStationSkeletonPreviewVisible));
        OnPropertyChanged(nameof(StationSkeletonProposedCount));
        OnPropertyChanged(nameof(StationSkeletonSummaryText));
        OnPropertyChanged(nameof(StationSkeletonApplyText));
        _applyCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
    }

    private void RaiseStationSetupChanged()
    {
        OnPropertyChanged(nameof(StationName));
        OnPropertyChanged(nameof(WaferType));
        OnPropertyChanged(nameof(AxisTravelText));
        OnPropertyChanged(nameof(TransportSpeedText));
        OnPropertyChanged(nameof(EntrySensorPositionText));
        OnPropertyChanged(nameof(ProcessSensorPositionText));
        OnPropertyChanged(nameof(CylinderTravelTimeText));
        RaiseStationSetupValidationChanged();
    }

    private void RaiseStationSetupValidationChanged()
    {
        OnPropertyChanged(nameof(IsStationNameValid));
        OnPropertyChanged(nameof(IsWaferTypeValid));
        OnPropertyChanged(nameof(IsAxisTravelValid));
        OnPropertyChanged(nameof(IsTransportSpeedValid));
        OnPropertyChanged(nameof(IsEntrySensorPositionValid));
        OnPropertyChanged(nameof(IsProcessSensorPositionValid));
        OnPropertyChanged(nameof(IsCylinderTravelTimeValid));
        OnPropertyChanged(nameof(HasStationSetupValidationError));
        OnPropertyChanged(nameof(StationSetupValidationText));
        _applyCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        _previewCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
    }

    private static bool TryPositiveDouble(string text, out double value) =>
        TryFiniteDouble(text, out value) && value > 0;

    private static bool TryFiniteDouble(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
         || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(key), args);
}
