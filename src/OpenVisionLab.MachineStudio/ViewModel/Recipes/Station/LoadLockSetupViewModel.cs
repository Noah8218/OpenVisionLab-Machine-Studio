using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class LoadLockSetupViewModel : ViewModelBase
{
    private readonly Func<LoadLockDefinition, int> _applyLoadLockSetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isVisible;
    private LoadLockDefinition? _savedSetup;
    private string? _outerDoorComponentId;
    private string? _innerDoorComponentId;
    private string? _evacuateCommandChannelId;
    private string? _ventCommandChannelId;
    private string? _vacuumReadySensorChannelId;
    private string? _atmosphereReadySensorChannelId;
    private string _pumpDownDurationText = string.Empty;
    private string _ventDurationText = string.Empty;

    public LoadLockSetupViewModel(
        Func<LoadLockDefinition, int> applyLoadLockSetup,
        Action clearWorkbenchPreviews)
    {
        _applyLoadLockSetup = applyLoadLockSetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => IsEditable && _project is not null);
        _applyCommand = new RelayCommand(_ => Apply(), _ => IsEditable && IsVisible && TryCreate(out LoadLockDefinition _));
        _cancelCommand = new RelayCommand(_ => ClearAll(), _ => IsVisible);
        _resetCommand = new RelayCommand(_ => Reset(), _ => IsEditable && IsVisible);
    }

    public ObservableCollection<LoadLockSetupOption> DoorOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> OutputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InputOptions { get; } = new();

    public ICommand PreviewCommand => _previewCommand;
    public ICommand ApplyCommand => _applyCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand ResetCommand => _resetCommand;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (!SetProperty(ref _isEditable, value)) return;
            RaiseCommandStates();
        }
    }

    public bool IsVisible => _isVisible;
    public string? OuterDoorComponentId { get => _outerDoorComponentId; set => SetSelection(ref _outerDoorComponentId, value, nameof(OuterDoorComponentId)); }
    public string? InnerDoorComponentId { get => _innerDoorComponentId; set => SetSelection(ref _innerDoorComponentId, value, nameof(InnerDoorComponentId)); }
    public string? EvacuateCommandChannelId { get => _evacuateCommandChannelId; set => SetSelection(ref _evacuateCommandChannelId, value, nameof(EvacuateCommandChannelId)); }
    public string? VentCommandChannelId { get => _ventCommandChannelId; set => SetSelection(ref _ventCommandChannelId, value, nameof(VentCommandChannelId)); }
    public string? VacuumReadySensorChannelId { get => _vacuumReadySensorChannelId; set => SetSelection(ref _vacuumReadySensorChannelId, value, nameof(VacuumReadySensorChannelId)); }
    public string? AtmosphereReadySensorChannelId { get => _atmosphereReadySensorChannelId; set => SetSelection(ref _atmosphereReadySensorChannelId, value, nameof(AtmosphereReadySensorChannelId)); }
    public string PumpDownDurationText { get => _pumpDownDurationText; set => SetText(ref _pumpDownDurationText, value, nameof(PumpDownDurationText)); }
    public string VentDurationText { get => _ventDurationText; set => SetText(ref _ventDurationText, value, nameof(VentDurationText)); }
    public bool IsOuterDoorComponentValid => IsDoor(OuterDoorComponentId) && !Same(OuterDoorComponentId, InnerDoorComponentId);
    public bool IsInnerDoorComponentValid => IsDoor(InnerDoorComponentId) && !Same(OuterDoorComponentId, InnerDoorComponentId);
    public bool IsEvacuateCommandChannelValid => IsChannel(EvacuateCommandChannelId, ChannelKind.DigitalOutput) && !Same(EvacuateCommandChannelId, VentCommandChannelId);
    public bool IsVentCommandChannelValid => IsChannel(VentCommandChannelId, ChannelKind.DigitalOutput) && !Same(EvacuateCommandChannelId, VentCommandChannelId);
    public bool IsVacuumReadySensorChannelValid => IsChannel(VacuumReadySensorChannelId, ChannelKind.DigitalInput) && !Same(VacuumReadySensorChannelId, AtmosphereReadySensorChannelId);
    public bool IsAtmosphereReadySensorChannelValid => IsChannel(AtmosphereReadySensorChannelId, ChannelKind.DigitalInput) && !Same(VacuumReadySensorChannelId, AtmosphereReadySensorChannelId);
    public bool IsPumpDownDurationValid => TryDuration(PumpDownDurationText, out _);
    public bool IsVentDurationValid => TryDuration(VentDurationText, out _);
    public bool HasMultipleLoadLocks => _project?.Devices.Count(device => device.Kind == DeviceKind.LoadLock) > 1;
    public bool HasLoadLockSetupValidationError => !TryCreate(out _);
    public bool HasValidationError => HasLoadLockSetupValidationError;
    public string LoadLockSetupValidationText => HasMultipleLoadLocks
        ? OpenVisionLanguageService.T("Connections.LoadLockSetupMultipleError")
        : HasLoadLockSetupValidationError
            ? OpenVisionLanguageService.T("Connections.LoadLockSetupValidationError")
            : OpenVisionLanguageService.T("Connections.LoadLockSetupValidationReady");
    public string ValidationText => LoadLockSetupValidationText;

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearAll();
        RaiseCommandStates();
    }

    public void ClearPreviewForCompetingSetup() => ClearAll();

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        (string? OuterDoor, string? InnerDoor, string? Evacuate, string? Vent, string? VacuumReady, string? AtmosphereReady, string PumpDown, string VentDuration)? draft = IsVisible ? CaptureDraft() : null;
        reloadWorkbench();
        if (draft is not null)
        {
            Preview();
            RestoreDraft(draft.Value);
        }
    }

    private void Preview()
    {
        if (_project is null) return;
        _clearWorkbenchPreviews();
        DoorOptions.Clear(); OutputOptions.Clear(); InputOptions.Clear();
        var layout = ResolveActiveLayout(_project);
        foreach (var component in (layout?.Components ?? [])
                     .Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder)
                     .OrderBy(component => component.Name, StringComparer.CurrentCulture)
                     .ThenBy(component => component.Id, StringComparer.Ordinal))
            DoorOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = Option(channel.Id, channel.Name);
            if (channel.Kind == ChannelKind.DigitalOutput) OutputOptions.Add(option);
            if (channel.Kind == ChannelKind.DigitalInput) InputOptions.Add(option);
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.LoadLock, LoadLock: not null }).ToArray();
        _savedSetup = existing.Length == 1 ? Clone(existing[0].LoadLock!) : null;
        var draft = _savedSetup ?? Suggest();
        AddMissing(DoorOptions, draft.OuterDoorComponentId);
        AddMissing(DoorOptions, draft.InnerDoorComponentId);
        AddMissing(OutputOptions, draft.EvacuateCommandChannelId);
        AddMissing(OutputOptions, draft.VentCommandChannelId);
        AddMissing(InputOptions, draft.VacuumReadySensorChannelId);
        AddMissing(InputOptions, draft.AtmosphereReadySensorChannelId);
        ApplyDraft(draft);
        _isVisible = true;
        RaiseChanged();
    }

    private void Apply()
    {
        if (!TryCreate(out var setup)) return;
        if (_applyLoadLockSetup(setup) > 0 || IsEquivalentToSaved(setup)) ClearAll();
        else Preview();
    }

    private void Reset() => ApplyDraft(_savedSetup is null ? Suggest() : Clone(_savedSetup));

    private void ClearAll()
    {
        _isVisible = false;
        _savedSetup = null;
        DoorOptions.Clear(); OutputOptions.Clear(); InputOptions.Clear();
        RaiseChanged();
    }

    private LoadLockDefinition Suggest()
    {
        var doors = DoorOptions.Take(2).Select(option => option.Id).ToArray();
        var outputs = OutputOptions.Take(2).Select(option => option.Id).ToArray();
        var inputs = InputOptions.Take(2).Select(option => option.Id).ToArray();
        var step = Math.Max(1, _project?.Simulation.FixedStepMilliseconds ?? 5);
        var duration = Math.Max(500, step);
        duration -= duration % step;
        return new LoadLockDefinition
        {
            OuterDoorComponentId = doors.ElementAtOrDefault(0) ?? string.Empty,
            InnerDoorComponentId = doors.ElementAtOrDefault(1) ?? string.Empty,
            EvacuateCommandChannelId = outputs.ElementAtOrDefault(0) ?? string.Empty,
            VentCommandChannelId = outputs.ElementAtOrDefault(1) ?? string.Empty,
            VacuumReadySensorChannelId = inputs.ElementAtOrDefault(0) ?? string.Empty,
            AtmosphereReadySensorChannelId = inputs.ElementAtOrDefault(1) ?? string.Empty,
            PumpDownDurationMilliseconds = duration,
            VentDurationMilliseconds = duration
        };
    }

    private void ApplyDraft(LoadLockDefinition setup)
    {
        _outerDoorComponentId = setup.OuterDoorComponentId;
        _innerDoorComponentId = setup.InnerDoorComponentId;
        _evacuateCommandChannelId = setup.EvacuateCommandChannelId;
        _ventCommandChannelId = setup.VentCommandChannelId;
        _vacuumReadySensorChannelId = setup.VacuumReadySensorChannelId;
        _atmosphereReadySensorChannelId = setup.AtmosphereReadySensorChannelId;
        _pumpDownDurationText = setup.PumpDownDurationMilliseconds.ToString(CultureInfo.CurrentCulture);
        _ventDurationText = setup.VentDurationMilliseconds.ToString(CultureInfo.CurrentCulture);
        RaiseValidationChanged();
    }

    private void SetSelection(ref string? field, string? value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName)) RaiseValidationChanged();
    }

    private void SetText(ref string field, string value, string propertyName)
    {
        if (SetProperty(ref field, value, propertyName)) RaiseValidationChanged();
    }

    private bool TryCreate(out LoadLockDefinition setup)
    {
        var pumpValid = TryDuration(PumpDownDurationText, out var pumpDuration);
        var ventValid = TryDuration(VentDurationText, out var ventDuration);
        setup = new LoadLockDefinition
        {
            OuterDoorComponentId = OuterDoorComponentId ?? string.Empty,
            InnerDoorComponentId = InnerDoorComponentId ?? string.Empty,
            EvacuateCommandChannelId = EvacuateCommandChannelId ?? string.Empty,
            VentCommandChannelId = VentCommandChannelId ?? string.Empty,
            VacuumReadySensorChannelId = VacuumReadySensorChannelId ?? string.Empty,
            AtmosphereReadySensorChannelId = AtmosphereReadySensorChannelId ?? string.Empty,
            PumpDownDurationMilliseconds = pumpDuration,
            VentDurationMilliseconds = ventDuration
        };
        return !HasMultipleLoadLocks && IsOuterDoorComponentValid && IsInnerDoorComponentValid
            && IsEvacuateCommandChannelValid && IsVentCommandChannelValid
            && IsVacuumReadySensorChannelValid && IsAtmosphereReadySensorChannelValid
            && pumpValid && ventValid;
    }

    private bool IsEquivalentToSaved(LoadLockDefinition setup) => _savedSetup is not null
        && _savedSetup.OuterDoorComponentId == setup.OuterDoorComponentId
        && _savedSetup.InnerDoorComponentId == setup.InnerDoorComponentId
        && _savedSetup.EvacuateCommandChannelId == setup.EvacuateCommandChannelId
        && _savedSetup.VentCommandChannelId == setup.VentCommandChannelId
        && _savedSetup.VacuumReadySensorChannelId == setup.VacuumReadySensorChannelId
        && _savedSetup.AtmosphereReadySensorChannelId == setup.AtmosphereReadySensorChannelId
        && _savedSetup.PumpDownDurationMilliseconds == setup.PumpDownDurationMilliseconds
        && _savedSetup.VentDurationMilliseconds == setup.VentDurationMilliseconds;

    private bool IsDoor(string? id) => !string.IsNullOrWhiteSpace(id)
        && ResolveActiveLayout(_project!)?.Components.Any(component => component.Kind == LayoutComponentKind.PneumaticCylinder && component.Id == id) == true;

    private bool IsChannel(string? id, ChannelKind kind) => !string.IsNullOrWhiteSpace(id)
        && _project?.Channels.Any(channel => channel.Kind == kind && channel.Id == id) == true;

    private bool TryDuration(string text, out int duration)
    {
        var step = Math.Max(1, _project?.Simulation.FixedStepMilliseconds ?? 5);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out duration)
            && duration > 0 && duration % step == 0;
    }

    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string id)
    {
        if (!string.IsNullOrWhiteSpace(id) && options.All(option => option.Id != id))
            options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})"));
    }

    private static LoadLockSetupOption Option(string id, string? name) => new(id, DisplayName(name, id));

    private static LoadLockDefinition Clone(LoadLockDefinition setup) => new()
    {
        OuterDoorComponentId = setup.OuterDoorComponentId,
        InnerDoorComponentId = setup.InnerDoorComponentId,
        EvacuateCommandChannelId = setup.EvacuateCommandChannelId,
        VentCommandChannelId = setup.VentCommandChannelId,
        VacuumReadySensorChannelId = setup.VacuumReadySensorChannelId,
        AtmosphereReadySensorChannelId = setup.AtmosphereReadySensorChannelId,
        PumpDownDurationMilliseconds = setup.PumpDownDurationMilliseconds,
        VentDurationMilliseconds = setup.VentDurationMilliseconds
    };

    private (string? OuterDoor, string? InnerDoor, string? Evacuate, string? Vent, string? VacuumReady, string? AtmosphereReady, string PumpDown, string VentDuration) CaptureDraft() =>
        (OuterDoorComponentId, InnerDoorComponentId, EvacuateCommandChannelId, VentCommandChannelId, VacuumReadySensorChannelId, AtmosphereReadySensorChannelId, PumpDownDurationText, VentDurationText);

    private void RestoreDraft((string? OuterDoor, string? InnerDoor, string? Evacuate, string? Vent, string? VacuumReady, string? AtmosphereReady, string PumpDown, string VentDuration) draft)
    {
        _outerDoorComponentId = draft.OuterDoor; _innerDoorComponentId = draft.InnerDoor;
        _evacuateCommandChannelId = draft.Evacuate; _ventCommandChannelId = draft.Vent;
        _vacuumReadySensorChannelId = draft.VacuumReady; _atmosphereReadySensorChannelId = draft.AtmosphereReady;
        _pumpDownDurationText = draft.PumpDown; _ventDurationText = draft.VentDuration;
        RaiseValidationChanged();
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(IsVisible));
        RaiseValidationChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
    }

    private void RaiseValidationChanged()
    {
        foreach (var property in new[]
        {
            nameof(OuterDoorComponentId), nameof(InnerDoorComponentId), nameof(EvacuateCommandChannelId),
            nameof(VentCommandChannelId), nameof(VacuumReadySensorChannelId), nameof(AtmosphereReadySensorChannelId),
            nameof(PumpDownDurationText), nameof(VentDurationText), nameof(IsOuterDoorComponentValid),
            nameof(IsInnerDoorComponentValid), nameof(IsEvacuateCommandChannelValid), nameof(IsVentCommandChannelValid),
            nameof(IsVacuumReadySensorChannelValid), nameof(IsAtmosphereReadySensorChannelValid),
            nameof(IsPumpDownDurationValid), nameof(IsVentDurationValid), nameof(HasMultipleLoadLocks),
            nameof(HasLoadLockSetupValidationError), nameof(LoadLockSetupValidationText)
            ,nameof(HasValidationError), nameof(ValidationText)
        }) OnPropertyChanged(property);
        _applyCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        _previewCommand.RaiseCanExecuteChanged();
        _applyCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project)
    {
        if (!string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId))
            return project.Layouts.FirstOrDefault(layout => layout.Id == project.Simulation.ActiveLayoutId);
        return project.Layouts.Count == 1 ? project.Layouts[0] : null;
    }

    private static string DisplayName(string? name, string id) => string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}";
}
