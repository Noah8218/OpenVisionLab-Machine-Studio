using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class InspectionHandoffSetupViewModel : ViewModelBase, IDisposable
{
    private readonly Func<InspectionHandoffDefinition, int> _applySetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isVisible;
    private InspectionHandoffDefinition? _savedSetup;
    private string? _cameraId;
    private string? _positionSensorChannelId;
    private string? _acceptedChannelId;
    private string? _readyChannelId;
    private string? _completeChannelId;
    private int _disposed;

    public InspectionHandoffSetupViewModel(Func<InspectionHandoffDefinition, int> applySetup, Action clearWorkbenchPreviews)
    {
        _applySetup = applySetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => !IsDisposed && IsEditable && _project is not null);
        _applyCommand = new RelayCommand(_ => Apply(), ignored => !IsDisposed && IsEditable && IsVisible && TryCreate(out _));
        _cancelCommand = new RelayCommand(_ => ClearPreviewForCompetingSetup(), _ => !IsDisposed && IsVisible);
        _resetCommand = new RelayCommand(_ => Reset(), _ => !IsDisposed && IsEditable && IsVisible);
    }

    public ObservableCollection<LoadLockSetupOption> CameraOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> InputOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> OutputOptions { get; } = new();

    public ICommand PreviewCommand => _previewCommand;
    public ICommand ApplyCommand => _applyCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand ResetCommand => _resetCommand;

    public bool IsEditable
    {
        get => _isEditable;
        set
        {
            if (IsDisposed || !SetProperty(ref _isEditable, value)) return;
            RaiseCommandStates();
        }
    }

    public bool IsVisible => _isVisible;
    public string? CameraId { get => _cameraId; set => SetSelection(ref _cameraId, value, nameof(CameraId)); }
    public string? PositionSensorChannelId { get => _positionSensorChannelId; set => SetSelection(ref _positionSensorChannelId, value, nameof(PositionSensorChannelId)); }
    public string? AcceptedChannelId { get => _acceptedChannelId; set => SetSelection(ref _acceptedChannelId, value, nameof(AcceptedChannelId)); }
    public string? ReadyChannelId { get => _readyChannelId; set => SetSelection(ref _readyChannelId, value, nameof(ReadyChannelId)); }
    public string? CompleteChannelId { get => _completeChannelId; set => SetSelection(ref _completeChannelId, value, nameof(CompleteChannelId)); }
    public bool IsCameraValid => IsCamera(CameraId);
    public bool IsPositionValid => IsChannel(PositionSensorChannelId, ChannelKind.DigitalInput);
    public bool IsAcceptedValid => IsChannel(AcceptedChannelId, ChannelKind.DigitalOutput);
    public bool IsReadyValid => IsChannel(ReadyChannelId, ChannelKind.DigitalInput);
    public bool IsCompleteValid => IsChannel(CompleteChannelId, ChannelKind.DigitalInput);
    public bool HasMultipleHandoffs => _project?.Devices.Count(device => device.Kind == DeviceKind.Inspection) > 1;
    public bool HasValidationError => !TryCreate(out _);
    public string ValidationText => OpenVisionLanguageService.T(
        HasMultipleHandoffs ? "Connections.InspectionHandoffSetupMultipleError" : HasValidationError
            ? "Connections.InspectionHandoffSetupValidationError"
            : "Connections.InspectionHandoffSetupValidationReady");

    public void Load(MachineProjectDocument project)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ClearPreviewForCompetingSetup();
        RaiseCommandStates();
    }

    internal void ClearPreviewForCompetingSetup()
    {
        if (IsDisposed) return;
        _isVisible = false;
        _savedSetup = null;
        CameraOptions.Clear(); InputOptions.Clear(); OutputOptions.Clear();
        RaiseChanged();
    }

    internal void RefreshLocalization(Action reloadWorkbench)
    {
        if (IsDisposed) return;
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
        if (IsDisposed || _project is null) return;
        _clearWorkbenchPreviews();
        CameraOptions.Clear(); InputOptions.Clear(); OutputOptions.Clear();
        foreach (var camera in _project.Devices.Where(device => device is { Kind: DeviceKind.Camera, Camera: not null }).OrderBy(device => device.Name, StringComparer.CurrentCulture).ThenBy(device => device.Id, StringComparer.Ordinal))
            CameraOptions.Add(Option(camera.Id, camera.Name));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = Option(channel.Id, channel.Name);
            if (channel.Kind == ChannelKind.DigitalInput) InputOptions.Add(option);
            if (channel.Kind == ChannelKind.DigitalOutput) OutputOptions.Add(option);
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Inspection, InspectionHandoff: not null }).ToArray();
        _savedSetup = existing.Length == 1 ? Clone(existing[0].InspectionHandoff!) : null;
        var draft = _savedSetup ?? Suggest();
        AddMissing(CameraOptions, draft.CameraId);
        foreach (var id in new[] { draft.InspectionPositionSensorChannelId, draft.InspectionReadyFeedbackChannelId, draft.InspectionCompleteFeedbackChannelId }) AddMissing(InputOptions, id);
        AddMissing(OutputOptions, draft.ResultAcceptedCommandChannelId);
        ApplyDraft(draft);
        _isVisible = true;
        RaiseChanged();
    }

    private void Apply()
    {
        if (IsDisposed) return;
        if (!TryCreate(out var setup)) return;
        if (_applySetup(setup) > 0 || IsEquivalentToSaved(setup)) ClearPreviewForCompetingSetup(); else Preview();
    }

    private void Reset()
    {
        if (!IsDisposed)
        {
            ApplyDraft(_savedSetup is null ? Suggest() : Clone(_savedSetup));
        }
    }

    private InspectionHandoffDefinition Suggest() => new()
    {
        CameraId = CameraOptions.FirstOrDefault()?.Id ?? string.Empty,
        InspectionPositionSensorChannelId = InputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        InspectionReadyFeedbackChannelId = InputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        InspectionCompleteFeedbackChannelId = InputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        ResultAcceptedCommandChannelId = OutputOptions.FirstOrDefault()?.Id ?? string.Empty
    };

    private void ApplyDraft(InspectionHandoffDefinition setup)
    {
        _cameraId = setup.CameraId; _positionSensorChannelId = setup.InspectionPositionSensorChannelId; _acceptedChannelId = setup.ResultAcceptedCommandChannelId;
        _readyChannelId = setup.InspectionReadyFeedbackChannelId; _completeChannelId = setup.InspectionCompleteFeedbackChannelId;
        RaiseValidationChanged();
    }

    private bool TryCreate(out InspectionHandoffDefinition setup)
    {
        var channels = new[] { PositionSensorChannelId, AcceptedChannelId, ReadyChannelId, CompleteChannelId };
        setup = new InspectionHandoffDefinition
        {
            CameraId = CameraId ?? string.Empty, InspectionPositionSensorChannelId = PositionSensorChannelId ?? string.Empty,
            ResultAcceptedCommandChannelId = AcceptedChannelId ?? string.Empty, InspectionReadyFeedbackChannelId = ReadyChannelId ?? string.Empty,
            InspectionCompleteFeedbackChannelId = CompleteChannelId ?? string.Empty
        };
        return !HasMultipleHandoffs && IsCameraValid && IsPositionValid && IsAcceptedValid && IsReadyValid && IsCompleteValid && Distinct(channels);
    }

    private bool IsEquivalentToSaved(InspectionHandoffDefinition setup) => _savedSetup is not null
        && Same(_savedSetup.CameraId, setup.CameraId) && Same(_savedSetup.InspectionPositionSensorChannelId, setup.InspectionPositionSensorChannelId)
        && Same(_savedSetup.ResultAcceptedCommandChannelId, setup.ResultAcceptedCommandChannelId) && Same(_savedSetup.InspectionReadyFeedbackChannelId, setup.InspectionReadyFeedbackChannelId)
        && Same(_savedSetup.InspectionCompleteFeedbackChannelId, setup.InspectionCompleteFeedbackChannelId);

    private InspectionHandoffDraft CaptureDraft() => new(CameraId, PositionSensorChannelId, AcceptedChannelId, ReadyChannelId, CompleteChannelId);

    private void RestoreDraft(InspectionHandoffDraft draft)
    {
        AddMissing(CameraOptions, draft.CameraId); foreach (var id in new[] { draft.PositionInputId, draft.ReadyInputId, draft.CompleteInputId }) AddMissing(InputOptions, id); AddMissing(OutputOptions, draft.AcceptedOutputId);
        _cameraId = draft.CameraId; _positionSensorChannelId = draft.PositionInputId; _acceptedChannelId = draft.AcceptedOutputId; _readyChannelId = draft.ReadyInputId; _completeChannelId = draft.CompleteInputId;
        RaiseValidationChanged();
    }

    private void SetSelection(ref string? field, string? value, string propertyName) { if (!IsDisposed && SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private bool IsCamera(string? id) => _project?.Devices.Any(device => device is { Kind: DeviceKind.Camera, Camera: not null } && Same(device.Id, id)) == true;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _isVisible = false;
        _savedSetup = null;
        CameraOptions.Clear();
        InputOptions.Clear();
        OutputOptions.Clear();
        RaiseChanged();
        _previewCommand.RaiseCanExecuteChanged();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static bool Distinct(string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static LoadLockSetupOption Option(string id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}");
    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string? id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !Same(option.Id, id))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static InspectionHandoffDefinition Clone(InspectionHandoffDefinition value) => new() { CameraId = value.CameraId, InspectionPositionSensorChannelId = value.InspectionPositionSensorChannelId, ResultAcceptedCommandChannelId = value.ResultAcceptedCommandChannelId, InspectionReadyFeedbackChannelId = value.InspectionReadyFeedbackChannelId, InspectionCompleteFeedbackChannelId = value.InspectionCompleteFeedbackChannelId };

    private sealed record InspectionHandoffDraft(string? CameraId, string? PositionInputId, string? AcceptedOutputId, string? ReadyInputId, string? CompleteInputId);
    private static readonly string[] ValidationProperties =
    [
        nameof(CameraId), nameof(PositionSensorChannelId), nameof(AcceptedChannelId), nameof(ReadyChannelId), nameof(CompleteChannelId), nameof(IsCameraValid), nameof(IsPositionValid), nameof(IsAcceptedValid), nameof(IsReadyValid), nameof(IsCompleteValid), nameof(HasMultipleHandoffs), nameof(HasValidationError), nameof(ValidationText)
    ];
}
