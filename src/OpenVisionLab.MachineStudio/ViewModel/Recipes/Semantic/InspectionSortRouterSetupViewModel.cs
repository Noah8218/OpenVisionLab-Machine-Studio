using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class InspectionSortRouterSetupViewModel : ViewModelBase
{
    private readonly Func<InspectionSortRouterDefinition, int> _applySetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isVisible;
    private InspectionSortRouterDefinition? _savedSetup;
    private string? _cameraId;
    private string? _passConveyorId;
    private string? _ngConveyorId;
    private string? _passFeedbackChannelId;
    private string? _ngFeedbackChannelId;

    public InspectionSortRouterSetupViewModel(Func<InspectionSortRouterDefinition, int> applySetup, Action clearWorkbenchPreviews)
    {
        _applySetup = applySetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => IsEditable && _project is not null);
        _applyCommand = new RelayCommand(_ => Apply(), ignored => IsEditable && IsVisible && TryCreate(out _));
        _cancelCommand = new RelayCommand(_ => ClearPreviewForCompetingSetup(), _ => IsVisible);
        _resetCommand = new RelayCommand(_ => Reset(), _ => IsEditable && IsVisible);
    }

    public ObservableCollection<LoadLockSetupOption> CameraOptions { get; } = new();
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
    public string? CameraId { get => _cameraId; set => SetSelection(ref _cameraId, value, nameof(CameraId)); }
    public string? PassConveyorId { get => _passConveyorId; set => SetSelection(ref _passConveyorId, value, nameof(PassConveyorId)); }
    public string? NgConveyorId { get => _ngConveyorId; set => SetSelection(ref _ngConveyorId, value, nameof(NgConveyorId)); }
    public string? PassFeedbackChannelId { get => _passFeedbackChannelId; set => SetSelection(ref _passFeedbackChannelId, value, nameof(PassFeedbackChannelId)); }
    public string? NgFeedbackChannelId { get => _ngFeedbackChannelId; set => SetSelection(ref _ngFeedbackChannelId, value, nameof(NgFeedbackChannelId)); }
    public bool IsCameraValid => IsCamera(CameraId);
    public bool IsPassConveyorValid => IsLayoutComponent(PassConveyorId, LayoutComponentKind.Conveyor);
    public bool IsNgConveyorValid => IsLayoutComponent(NgConveyorId, LayoutComponentKind.Conveyor) && !Same(PassConveyorId, NgConveyorId);
    public bool IsPassFeedbackValid => IsChannel(PassFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsNgFeedbackValid => IsChannel(NgFeedbackChannelId, ChannelKind.DigitalInput) && !Same(PassFeedbackChannelId, NgFeedbackChannelId);
    public bool HasMultipleSortRouters => _project?.Devices.Count(device => device.Kind == DeviceKind.Sorter) > 1;
    public bool HasValidationError => !TryCreate(out _);
    public string ValidationText => OpenVisionLanguageService.T(
        HasMultipleSortRouters ? "Connections.InspectionSortSetupMultipleError" : HasValidationError
            ? "Connections.InspectionSortSetupValidationError"
            : "Connections.InspectionSortSetupValidationReady");

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
        CameraOptions.Clear(); ConveyorOptions.Clear(); InputOptions.Clear();
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
        CameraOptions.Clear(); ConveyorOptions.Clear(); InputOptions.Clear();
        var layout = ResolveActiveLayout(_project);
        foreach (var camera in _project.Devices.Where(device => device is { Kind: DeviceKind.Camera, Camera: not null }).OrderBy(device => device.Name, StringComparer.CurrentCulture).ThenBy(device => device.Id, StringComparer.Ordinal)) CameraOptions.Add(Option(camera.Id, camera.Name));
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Conveyor).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal)) ConveyorOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.Where(channel => channel.Kind == ChannelKind.DigitalInput).OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal)) InputOptions.Add(Option(channel.Id, channel.Name));
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Sorter, InspectionSortRouter: not null }).ToArray();
        _savedSetup = existing.Length == 1 ? Clone(existing[0].InspectionSortRouter!) : null;
        var draft = _savedSetup ?? Suggest();
        AddMissing(CameraOptions, draft.CameraId); AddMissing(ConveyorOptions, draft.PassConveyorComponentId); AddMissing(ConveyorOptions, draft.NgConveyorComponentId); AddMissing(InputOptions, draft.PassRoutedFeedbackChannelId); AddMissing(InputOptions, draft.NgRoutedFeedbackChannelId);
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

    private InspectionSortRouterDefinition Suggest() => new()
    {
        CameraId = CameraOptions.FirstOrDefault()?.Id ?? string.Empty,
        PassConveyorComponentId = ConveyorOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        NgConveyorComponentId = ConveyorOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        PassRoutedFeedbackChannelId = InputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        NgRoutedFeedbackChannelId = InputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty
    };

    private void ApplyDraft(InspectionSortRouterDefinition setup)
    {
        _cameraId = setup.CameraId; _passConveyorId = setup.PassConveyorComponentId; _ngConveyorId = setup.NgConveyorComponentId;
        _passFeedbackChannelId = setup.PassRoutedFeedbackChannelId; _ngFeedbackChannelId = setup.NgRoutedFeedbackChannelId;
        RaiseValidationChanged();
    }

    private bool TryCreate(out InspectionSortRouterDefinition setup)
    {
        setup = new InspectionSortRouterDefinition
        {
            CameraId = CameraId ?? string.Empty, PassConveyorComponentId = PassConveyorId ?? string.Empty, NgConveyorComponentId = NgConveyorId ?? string.Empty,
            PassRoutedFeedbackChannelId = PassFeedbackChannelId ?? string.Empty, NgRoutedFeedbackChannelId = NgFeedbackChannelId ?? string.Empty
        };
        return !HasMultipleSortRouters && IsCameraValid && IsPassConveyorValid && IsNgConveyorValid && IsPassFeedbackValid && IsNgFeedbackValid;
    }

    private bool IsEquivalentToSaved(InspectionSortRouterDefinition setup) => _savedSetup is not null
        && Same(_savedSetup.CameraId, setup.CameraId) && Same(_savedSetup.PassConveyorComponentId, setup.PassConveyorComponentId) && Same(_savedSetup.NgConveyorComponentId, setup.NgConveyorComponentId)
        && Same(_savedSetup.PassRoutedFeedbackChannelId, setup.PassRoutedFeedbackChannelId) && Same(_savedSetup.NgRoutedFeedbackChannelId, setup.NgRoutedFeedbackChannelId);

    private InspectionSortRouterDraft CaptureDraft() => new(CameraId, PassConveyorId, NgConveyorId, PassFeedbackChannelId, NgFeedbackChannelId);

    private void RestoreDraft(InspectionSortRouterDraft draft)
    {
        AddMissing(CameraOptions, draft.CameraId); AddMissing(ConveyorOptions, draft.PassConveyorId); AddMissing(ConveyorOptions, draft.NgConveyorId); AddMissing(InputOptions, draft.PassFeedbackInputId); AddMissing(InputOptions, draft.NgFeedbackInputId);
        _cameraId = draft.CameraId; _passConveyorId = draft.PassConveyorId; _ngConveyorId = draft.NgConveyorId; _passFeedbackChannelId = draft.PassFeedbackInputId; _ngFeedbackChannelId = draft.NgFeedbackInputId;
        RaiseValidationChanged();
    }

    private void SetSelection(ref string? field, string? value, string propertyName) { if (SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private MachineLayoutDefinition? ActiveLayout() => _project is null ? null : ResolveActiveLayout(_project);
    private bool IsLayoutComponent(string? id, LayoutComponentKind kind) => ActiveLayout()?.Components.Any(component => component.Kind == kind && Same(component.Id, id)) == true;
    private bool IsChannel(string? id, ChannelKind kind) => _project?.Channels.Any(channel => channel.Kind == kind && Same(channel.Id, id)) == true;
    private bool IsCamera(string? id) => _project?.Devices.Any(device => device is { Kind: DeviceKind.Camera, Camera: not null } && Same(device.Id, id)) == true;

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

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static LoadLockSetupOption Option(string id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}");
    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string? id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !Same(option.Id, id))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project) => !string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId) ? project.Layouts.FirstOrDefault(layout => Same(layout.Id, project.Simulation.ActiveLayoutId)) : project.Layouts.Count == 1 ? project.Layouts[0] : null;
    private static InspectionSortRouterDefinition Clone(InspectionSortRouterDefinition value) => new() { CameraId = value.CameraId, PassConveyorComponentId = value.PassConveyorComponentId, NgConveyorComponentId = value.NgConveyorComponentId, PassRoutedFeedbackChannelId = value.PassRoutedFeedbackChannelId, NgRoutedFeedbackChannelId = value.NgRoutedFeedbackChannelId };

    private sealed record InspectionSortRouterDraft(string? CameraId, string? PassConveyorId, string? NgConveyorId, string? PassFeedbackInputId, string? NgFeedbackInputId);
    private static readonly string[] ValidationProperties =
    [
        nameof(CameraId), nameof(PassConveyorId), nameof(NgConveyorId), nameof(PassFeedbackChannelId), nameof(NgFeedbackChannelId), nameof(IsCameraValid), nameof(IsPassConveyorValid), nameof(IsNgConveyorValid), nameof(IsPassFeedbackValid), nameof(IsNgFeedbackValid), nameof(HasMultipleSortRouters), nameof(HasValidationError), nameof(ValidationText)
    ];
}
