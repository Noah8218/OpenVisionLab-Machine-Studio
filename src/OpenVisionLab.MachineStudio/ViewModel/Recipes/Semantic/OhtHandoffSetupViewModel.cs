using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class OhtHandoffSetupViewModel : ViewModelBase
{
    private readonly Func<OhtHandoffDefinition, int> _applySetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isVisible;
    private OhtHandoffDefinition? _savedSetup;
    private string? _transportConveyorId;
    private string? _routeAvailableChannelId;
    private string? _vehicleDockedChannelId;
    private string? _loadPortReadyChannelId;
    private string? _carrierReceivedChannelId;
    private string? _handoffReadyChannelId;
    private string? _carrierTransferredChannelId;

    public OhtHandoffSetupViewModel(Func<OhtHandoffDefinition, int> applySetup, Action clearWorkbenchPreviews)
    {
        _applySetup = applySetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => IsEditable && _project is not null);
        _applyCommand = new RelayCommand(_ => Apply(), ignored => IsEditable && IsVisible && TryCreate(out _));
        _cancelCommand = new RelayCommand(_ => ClearPreviewForCompetingSetup(), _ => IsVisible);
        _resetCommand = new RelayCommand(_ => Reset(), _ => IsEditable && IsVisible);
    }

    public ObservableCollection<LoadLockSetupOption> ConveyorOptions { get; } = new();
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
    public string? TransportConveyorId { get => _transportConveyorId; set => SetSelection(ref _transportConveyorId, value, nameof(TransportConveyorId)); }
    public string? RouteAvailableChannelId { get => _routeAvailableChannelId; set => SetSelection(ref _routeAvailableChannelId, value, nameof(RouteAvailableChannelId)); }
    public string? VehicleDockedChannelId { get => _vehicleDockedChannelId; set => SetSelection(ref _vehicleDockedChannelId, value, nameof(VehicleDockedChannelId)); }
    public string? LoadPortReadyChannelId { get => _loadPortReadyChannelId; set => SetSelection(ref _loadPortReadyChannelId, value, nameof(LoadPortReadyChannelId)); }
    public string? CarrierReceivedChannelId { get => _carrierReceivedChannelId; set => SetSelection(ref _carrierReceivedChannelId, value, nameof(CarrierReceivedChannelId)); }
    public string? HandoffReadyChannelId { get => _handoffReadyChannelId; set => SetSelection(ref _handoffReadyChannelId, value, nameof(HandoffReadyChannelId)); }
    public string? CarrierTransferredChannelId { get => _carrierTransferredChannelId; set => SetSelection(ref _carrierTransferredChannelId, value, nameof(CarrierTransferredChannelId)); }
    public bool IsTransportConveyorValid => IsLayoutComponent(TransportConveyorId, LayoutComponentKind.Conveyor);
    public bool IsRouteAvailableValid => IsChannel(RouteAvailableChannelId, ChannelKind.DigitalInput);
    public bool IsVehicleDockedValid => IsChannel(VehicleDockedChannelId, ChannelKind.DigitalInput);
    public bool IsLoadPortReadyValid => IsChannel(LoadPortReadyChannelId, ChannelKind.DigitalInput);
    public bool IsCarrierReceivedValid => IsChannel(CarrierReceivedChannelId, ChannelKind.DigitalInput);
    public bool IsHandoffReadyValid => IsChannel(HandoffReadyChannelId, ChannelKind.DigitalInput);
    public bool IsCarrierTransferredValid => IsChannel(CarrierTransferredChannelId, ChannelKind.DigitalInput);
    public bool HasMultipleOhtHandoffs => _project?.Devices.Count(device => device.Kind == DeviceKind.Oht) > 1;
    public bool HasValidationError => !TryCreate(out _);
    public string ValidationText => OpenVisionLanguageService.T(
        HasMultipleOhtHandoffs ? "Connections.OhtSetupMultipleError" : HasValidationError
            ? "Connections.OhtSetupValidationError"
            : "Connections.OhtSetupValidationReady");

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearPreviewForCompetingSetup();
        RaiseCommandStates();
    }

    internal void ClearPreviewForCompetingSetup()
    {
        _isVisible = false;
        _savedSetup = null;
        ConveyorOptions.Clear(); InputOptions.Clear();
        RaiseChanged();
    }

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        var draft = IsVisible ? CaptureDraft() : null;
        reloadWorkbench();
        if (draft is not null)
        {
            Preview();
            RestoreDraft(draft);
        }
    }

    private void Preview()
    {
        if (_project is null) return;
        _clearWorkbenchPreviews();
        ConveyorOptions.Clear(); InputOptions.Clear();
        var layout = ResolveActiveLayout(_project);
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Conveyor).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) ConveyorOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalInput).OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal)) InputOptions.Add(Option(channel.Id, channel.Name));
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Oht, OhtHandoff: not null }).ToArray();
        _savedSetup = existing.Length == 1 ? Clone(existing[0].OhtHandoff!) : null;
        var draft = _savedSetup ?? Suggest();
        AddMissing(ConveyorOptions, draft.TransportConveyorComponentId);
        foreach (var id in new[] { draft.RouteAvailableSensorChannelId, draft.VehicleDockedSensorChannelId, draft.LoadPortReadySensorChannelId, draft.CarrierReceivedSensorChannelId, draft.HandoffReadyFeedbackChannelId, draft.CarrierTransferredFeedbackChannelId }) AddMissing(InputOptions, id);
        ApplyDraft(draft);
        _isVisible = true;
        RaiseChanged();
    }

    private void Apply()
    {
        if (!TryCreate(out var setup)) return;
        if (_applySetup(setup) > 0 || IsEquivalentToSaved(setup)) ClearPreviewForCompetingSetup(); else Preview();
    }

    private void Reset() => ApplyDraft(_savedSetup is null ? Suggest() : Clone(_savedSetup));

    private OhtHandoffDefinition Suggest() => new()
    {
        TransportConveyorComponentId = ConveyorOptions.FirstOrDefault()?.Id ?? string.Empty,
        RouteAvailableSensorChannelId = InputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        VehicleDockedSensorChannelId = InputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        LoadPortReadySensorChannelId = InputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        CarrierReceivedSensorChannelId = InputOptions.ElementAtOrDefault(3)?.Id ?? string.Empty,
        HandoffReadyFeedbackChannelId = InputOptions.ElementAtOrDefault(4)?.Id ?? string.Empty,
        CarrierTransferredFeedbackChannelId = InputOptions.ElementAtOrDefault(5)?.Id ?? string.Empty
    };

    private void ApplyDraft(OhtHandoffDefinition setup)
    {
        _transportConveyorId = setup.TransportConveyorComponentId; _routeAvailableChannelId = setup.RouteAvailableSensorChannelId; _vehicleDockedChannelId = setup.VehicleDockedSensorChannelId;
        _loadPortReadyChannelId = setup.LoadPortReadySensorChannelId; _carrierReceivedChannelId = setup.CarrierReceivedSensorChannelId; _handoffReadyChannelId = setup.HandoffReadyFeedbackChannelId; _carrierTransferredChannelId = setup.CarrierTransferredFeedbackChannelId;
        RaiseValidationChanged();
    }

    private bool TryCreate(out OhtHandoffDefinition setup)
    {
        var channels = new[] { RouteAvailableChannelId, VehicleDockedChannelId, LoadPortReadyChannelId, CarrierReceivedChannelId, HandoffReadyChannelId, CarrierTransferredChannelId };
        setup = new OhtHandoffDefinition
        {
            TransportConveyorComponentId = TransportConveyorId ?? string.Empty, RouteAvailableSensorChannelId = RouteAvailableChannelId ?? string.Empty,
            VehicleDockedSensorChannelId = VehicleDockedChannelId ?? string.Empty, LoadPortReadySensorChannelId = LoadPortReadyChannelId ?? string.Empty,
            CarrierReceivedSensorChannelId = CarrierReceivedChannelId ?? string.Empty, HandoffReadyFeedbackChannelId = HandoffReadyChannelId ?? string.Empty,
            CarrierTransferredFeedbackChannelId = CarrierTransferredChannelId ?? string.Empty
        };
        return !HasMultipleOhtHandoffs && IsTransportConveyorValid && Distinct(channels) && IsRouteAvailableValid && IsVehicleDockedValid && IsLoadPortReadyValid && IsCarrierReceivedValid && IsHandoffReadyValid && IsCarrierTransferredValid;
    }

    private bool IsEquivalentToSaved(OhtHandoffDefinition setup) => _savedSetup is not null
        && Same(_savedSetup.TransportConveyorComponentId, setup.TransportConveyorComponentId) && Same(_savedSetup.RouteAvailableSensorChannelId, setup.RouteAvailableSensorChannelId)
        && Same(_savedSetup.VehicleDockedSensorChannelId, setup.VehicleDockedSensorChannelId) && Same(_savedSetup.LoadPortReadySensorChannelId, setup.LoadPortReadySensorChannelId)
        && Same(_savedSetup.CarrierReceivedSensorChannelId, setup.CarrierReceivedSensorChannelId) && Same(_savedSetup.HandoffReadyFeedbackChannelId, setup.HandoffReadyFeedbackChannelId)
        && Same(_savedSetup.CarrierTransferredFeedbackChannelId, setup.CarrierTransferredFeedbackChannelId);

    private OhtHandoffDraft CaptureDraft() => new(TransportConveyorId, RouteAvailableChannelId, VehicleDockedChannelId, LoadPortReadyChannelId, CarrierReceivedChannelId, HandoffReadyChannelId, CarrierTransferredChannelId);

    private void RestoreDraft(OhtHandoffDraft draft)
    {
        AddMissing(ConveyorOptions, draft.TransportConveyorId); foreach (var id in new[] { draft.RouteAvailableInputId, draft.VehicleDockedInputId, draft.LoadPortReadyInputId, draft.CarrierReceivedInputId, draft.HandoffReadyInputId, draft.CarrierTransferredInputId }) AddMissing(InputOptions, id);
        _transportConveyorId = draft.TransportConveyorId; _routeAvailableChannelId = draft.RouteAvailableInputId; _vehicleDockedChannelId = draft.VehicleDockedInputId; _loadPortReadyChannelId = draft.LoadPortReadyInputId;
        _carrierReceivedChannelId = draft.CarrierReceivedInputId; _handoffReadyChannelId = draft.HandoffReadyInputId; _carrierTransferredChannelId = draft.CarrierTransferredInputId;
        RaiseValidationChanged();
    }

    private void SetSelection(ref string? field, string? value, string propertyName) { if (SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private MachineLayoutDefinition? ActiveLayout() => _project is null ? null : ResolveActiveLayout(_project);
    private bool IsLayoutComponent(string? id, LayoutComponentKind kind) => ActiveLayout()?.Components.Any(component => component.Kind == kind && Same(component.Id, id)) == true;
    private bool IsChannel(string? id, ChannelKind kind) => _project?.Channels.Any(channel => channel.Kind == kind && Same(channel.Id, id)) == true;

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(IsVisible));
        RaiseValidationChanged();
        _cancelCommand.RaiseCanExecuteChanged(); _resetCommand.RaiseCanExecuteChanged();
    }

    private void RaiseValidationChanged()
    {
        foreach (var property in ValidationProperties) OnPropertyChanged(property);
        _applyCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        _previewCommand.RaiseCanExecuteChanged(); _applyCommand.RaiseCanExecuteChanged(); _cancelCommand.RaiseCanExecuteChanged(); _resetCommand.RaiseCanExecuteChanged();
    }

    private static bool Distinct(string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static LoadLockSetupOption Option(string id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}");
    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string? id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !Same(option.Id, id))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project) => !string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId) ? project.Layouts.FirstOrDefault(layout => Same(layout.Id, project.Simulation.ActiveLayoutId)) : project.Layouts.Count == 1 ? project.Layouts[0] : null;
    private static OhtHandoffDefinition Clone(OhtHandoffDefinition value) => new() { TransportConveyorComponentId = value.TransportConveyorComponentId, RouteAvailableSensorChannelId = value.RouteAvailableSensorChannelId, VehicleDockedSensorChannelId = value.VehicleDockedSensorChannelId, LoadPortReadySensorChannelId = value.LoadPortReadySensorChannelId, CarrierReceivedSensorChannelId = value.CarrierReceivedSensorChannelId, HandoffReadyFeedbackChannelId = value.HandoffReadyFeedbackChannelId, CarrierTransferredFeedbackChannelId = value.CarrierTransferredFeedbackChannelId };

    private sealed record OhtHandoffDraft(string? TransportConveyorId, string? RouteAvailableInputId, string? VehicleDockedInputId, string? LoadPortReadyInputId, string? CarrierReceivedInputId, string? HandoffReadyInputId, string? CarrierTransferredInputId);
    private static readonly string[] ValidationProperties =
    [
        nameof(TransportConveyorId), nameof(RouteAvailableChannelId), nameof(VehicleDockedChannelId), nameof(LoadPortReadyChannelId), nameof(CarrierReceivedChannelId), nameof(HandoffReadyChannelId), nameof(CarrierTransferredChannelId), nameof(IsTransportConveyorValid), nameof(IsRouteAvailableValid), nameof(IsVehicleDockedValid), nameof(IsLoadPortReadyValid), nameof(IsCarrierReceivedValid), nameof(IsHandoffReadyValid), nameof(IsCarrierTransferredValid), nameof(HasMultipleOhtHandoffs), nameof(HasValidationError), nameof(ValidationText)
    ];
}
