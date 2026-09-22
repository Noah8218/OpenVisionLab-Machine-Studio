using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class WaferHandlerSetupViewModel : ViewModelBase, IDisposable
{
    private readonly Func<WaferHandlerDefinition, int> _applySetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isVisible;
    private WaferHandlerDefinition? _savedSetup;
    private string? _horizontalAxisId;
    private string? _verticalAxisId;
    private string? _workpieceComponentId;
    private string? _sourcePresentSensorChannelId;
    private string? _gateOpenSensorChannelId;
    private string? _pickCommandChannelId;
    private string? _placeCommandChannelId;
    private string? _holdingFeedbackChannelId;
    private string? _placedFeedbackChannelId;
    private string _pickHorizontalText = string.Empty;
    private string _pickVerticalText = string.Empty;
    private string _placeHorizontalText = string.Empty;
    private string _placeVerticalText = string.Empty;
    private int _disposed;

    public WaferHandlerSetupViewModel(Func<WaferHandlerDefinition, int> applySetup, Action clearWorkbenchPreviews)
    {
        _applySetup = applySetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => !IsDisposed && IsEditable && _project is not null);
        _applyCommand = new RelayCommand(_ => Apply(), ignored => !IsDisposed && IsEditable && IsVisible && TryCreate(out _));
        _cancelCommand = new RelayCommand(_ => ClearPreviewForCompetingSetup(), _ => !IsDisposed && IsVisible);
        _resetCommand = new RelayCommand(_ => Reset(), _ => !IsDisposed && IsEditable && IsVisible);
    }

    public ObservableCollection<LoadLockSetupOption> AxisOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> WorkpieceOptions { get; } = new();
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
    public string? HorizontalAxisId { get => _horizontalAxisId; set => SetSelection(ref _horizontalAxisId, value, nameof(HorizontalAxisId)); }
    public string? VerticalAxisId { get => _verticalAxisId; set => SetSelection(ref _verticalAxisId, value, nameof(VerticalAxisId)); }
    public string? WorkpieceComponentId { get => _workpieceComponentId; set => SetSelection(ref _workpieceComponentId, value, nameof(WorkpieceComponentId)); }
    public string? SourcePresentSensorChannelId { get => _sourcePresentSensorChannelId; set => SetSelection(ref _sourcePresentSensorChannelId, value, nameof(SourcePresentSensorChannelId)); }
    public string? GateOpenSensorChannelId { get => _gateOpenSensorChannelId; set => SetSelection(ref _gateOpenSensorChannelId, value, nameof(GateOpenSensorChannelId)); }
    public string? PickCommandChannelId { get => _pickCommandChannelId; set => SetSelection(ref _pickCommandChannelId, value, nameof(PickCommandChannelId)); }
    public string? PlaceCommandChannelId { get => _placeCommandChannelId; set => SetSelection(ref _placeCommandChannelId, value, nameof(PlaceCommandChannelId)); }
    public string? HoldingFeedbackChannelId { get => _holdingFeedbackChannelId; set => SetSelection(ref _holdingFeedbackChannelId, value, nameof(HoldingFeedbackChannelId)); }
    public string? PlacedFeedbackChannelId { get => _placedFeedbackChannelId; set => SetSelection(ref _placedFeedbackChannelId, value, nameof(PlacedFeedbackChannelId)); }
    public string PickHorizontalText { get => _pickHorizontalText; set => SetText(ref _pickHorizontalText, value, nameof(PickHorizontalText)); }
    public string PickVerticalText { get => _pickVerticalText; set => SetText(ref _pickVerticalText, value, nameof(PickVerticalText)); }
    public string PlaceHorizontalText { get => _placeHorizontalText; set => SetText(ref _placeHorizontalText, value, nameof(PlaceHorizontalText)); }
    public string PlaceVerticalText { get => _placeVerticalText; set => SetText(ref _placeVerticalText, value, nameof(PlaceVerticalText)); }
    public bool HasMultipleHandlers => _project?.Devices.Count(device => device.Kind == DeviceKind.Handler) > 1;
    public bool IsHorizontalAxisValid => IsLinearAxis(HorizontalAxisId);
    public bool IsVerticalAxisValid => IsLinearAxis(VerticalAxisId) && !Same(HorizontalAxisId, VerticalAxisId);
    public bool IsWorkpieceValid => IsLayoutComponent(WorkpieceComponentId, LayoutComponentKind.Workpiece);
    public bool IsSourcePresentValid => IsChannel(SourcePresentSensorChannelId, ChannelKind.DigitalInput);
    public bool IsGateOpenValid => IsChannel(GateOpenSensorChannelId, ChannelKind.DigitalInput);
    public bool IsPickCommandValid => IsChannel(PickCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsPlaceCommandValid => IsChannel(PlaceCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsHoldingFeedbackValid => IsChannel(HoldingFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsPlacedFeedbackValid => IsChannel(PlacedFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsPickHorizontalValid => IsAxisPosition(HorizontalAxisId, PickHorizontalText);
    public bool IsPickVerticalValid => IsAxisPosition(VerticalAxisId, PickVerticalText);
    public bool IsPlaceHorizontalValid => IsAxisPosition(HorizontalAxisId, PlaceHorizontalText);
    public bool IsPlaceVerticalValid => IsAxisPosition(VerticalAxisId, PlaceVerticalText);
    public bool HasValidationError => !TryCreate(out _);
    public string ValidationText => OpenVisionLanguageService.T(
        HasMultipleHandlers ? "Connections.WaferHandlerSetupMultipleError" : HasValidationError
            ? "Connections.WaferHandlerSetupValidationError"
            : "Connections.WaferHandlerSetupValidationReady");

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
        AxisOptions.Clear(); WorkpieceOptions.Clear(); InputOptions.Clear(); OutputOptions.Clear();
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
        AxisOptions.Clear(); WorkpieceOptions.Clear(); InputOptions.Clear(); OutputOptions.Clear();
        var layout = ResolveActiveLayout(_project);
        foreach (var axis in _project.Axes.Where(axis => axis.Kind == AxisKind.Linear).OrderBy(axis => axis.Name, StringComparer.CurrentCulture).ThenBy(axis => axis.Id, StringComparer.Ordinal))
            AxisOptions.Add(Option(axis.Id, axis.Name));
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.Workpiece).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal))
            WorkpieceOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = Option(channel.Id, channel.Name);
            if (channel.Kind == ChannelKind.DigitalInput) InputOptions.Add(option);
            if (channel.Kind == ChannelKind.DigitalOutput) OutputOptions.Add(option);
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Handler, WaferHandler: not null }).ToArray();
        _savedSetup = existing.Length == 1 ? Clone(existing[0].WaferHandler!) : null;
        var draft = _savedSetup ?? Suggest();
        AddMissing(AxisOptions, draft.HorizontalAxisId); AddMissing(AxisOptions, draft.VerticalAxisId); AddMissing(WorkpieceOptions, draft.WorkpieceComponentId);
        foreach (var id in new[] { draft.SourcePresentSensorChannelId, draft.GateOpenSensorChannelId, draft.HoldingFeedbackChannelId, draft.PlacedFeedbackChannelId }) AddMissing(InputOptions, id);
        foreach (var id in new[] { draft.PickCommandChannelId, draft.PlaceCommandChannelId }) AddMissing(OutputOptions, id);
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

    private WaferHandlerDefinition Suggest() => new()
    {
        HorizontalAxisId = AxisOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        VerticalAxisId = AxisOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        WorkpieceComponentId = WorkpieceOptions.FirstOrDefault()?.Id ?? string.Empty,
        SourcePresentSensorChannelId = InputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        GateOpenSensorChannelId = InputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        HoldingFeedbackChannelId = InputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        PlacedFeedbackChannelId = InputOptions.ElementAtOrDefault(3)?.Id ?? string.Empty,
        PickCommandChannelId = OutputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        PlaceCommandChannelId = OutputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        PickHorizontalPosition = AxisLimit(AxisOptions.ElementAtOrDefault(0)?.Id, false),
        PickVerticalPosition = AxisLimit(AxisOptions.ElementAtOrDefault(1)?.Id, false),
        PlaceHorizontalPosition = AxisLimit(AxisOptions.ElementAtOrDefault(0)?.Id, true),
        PlaceVerticalPosition = AxisLimit(AxisOptions.ElementAtOrDefault(1)?.Id, true)
    };

    private void ApplyDraft(WaferHandlerDefinition setup)
    {
        _horizontalAxisId = setup.HorizontalAxisId; _verticalAxisId = setup.VerticalAxisId; _workpieceComponentId = setup.WorkpieceComponentId;
        _sourcePresentSensorChannelId = setup.SourcePresentSensorChannelId; _gateOpenSensorChannelId = setup.GateOpenSensorChannelId;
        _pickCommandChannelId = setup.PickCommandChannelId; _placeCommandChannelId = setup.PlaceCommandChannelId;
        _holdingFeedbackChannelId = setup.HoldingFeedbackChannelId; _placedFeedbackChannelId = setup.PlacedFeedbackChannelId;
        _pickHorizontalText = Format(setup.PickHorizontalPosition); _pickVerticalText = Format(setup.PickVerticalPosition);
        _placeHorizontalText = Format(setup.PlaceHorizontalPosition); _placeVerticalText = Format(setup.PlaceVerticalPosition);
        RaiseValidationChanged();
    }

    private bool TryCreate(out WaferHandlerDefinition setup)
    {
        var channels = new[] { SourcePresentSensorChannelId, GateOpenSensorChannelId, PickCommandChannelId, PlaceCommandChannelId, HoldingFeedbackChannelId, PlacedFeedbackChannelId };
        setup = new WaferHandlerDefinition
        {
            HorizontalAxisId = HorizontalAxisId ?? string.Empty, VerticalAxisId = VerticalAxisId ?? string.Empty, WorkpieceComponentId = WorkpieceComponentId ?? string.Empty,
            SourcePresentSensorChannelId = SourcePresentSensorChannelId ?? string.Empty, GateOpenSensorChannelId = GateOpenSensorChannelId ?? string.Empty,
            PickCommandChannelId = PickCommandChannelId ?? string.Empty, PlaceCommandChannelId = PlaceCommandChannelId ?? string.Empty,
            HoldingFeedbackChannelId = HoldingFeedbackChannelId ?? string.Empty, PlacedFeedbackChannelId = PlacedFeedbackChannelId ?? string.Empty,
            PickHorizontalPosition = Parse(PickHorizontalText), PickVerticalPosition = Parse(PickVerticalText), PlaceHorizontalPosition = Parse(PlaceHorizontalText), PlaceVerticalPosition = Parse(PlaceVerticalText)
        };
        return !HasMultipleHandlers && IsHorizontalAxisValid && IsVerticalAxisValid && IsWorkpieceValid && Distinct(channels)
            && IsSourcePresentValid && IsGateOpenValid && IsPickCommandValid && IsPlaceCommandValid && IsHoldingFeedbackValid && IsPlacedFeedbackValid
            && IsPickHorizontalValid && IsPickVerticalValid && IsPlaceHorizontalValid && IsPlaceVerticalValid;
    }

    private bool IsEquivalentToSaved(WaferHandlerDefinition setup) => _savedSetup is not null
        && Same(_savedSetup.HorizontalAxisId, setup.HorizontalAxisId) && Same(_savedSetup.VerticalAxisId, setup.VerticalAxisId) && Same(_savedSetup.WorkpieceComponentId, setup.WorkpieceComponentId)
        && Same(_savedSetup.SourcePresentSensorChannelId, setup.SourcePresentSensorChannelId) && Same(_savedSetup.GateOpenSensorChannelId, setup.GateOpenSensorChannelId)
        && Same(_savedSetup.PickCommandChannelId, setup.PickCommandChannelId) && Same(_savedSetup.PlaceCommandChannelId, setup.PlaceCommandChannelId)
        && Same(_savedSetup.HoldingFeedbackChannelId, setup.HoldingFeedbackChannelId) && Same(_savedSetup.PlacedFeedbackChannelId, setup.PlacedFeedbackChannelId)
        && _savedSetup.PickHorizontalPosition == setup.PickHorizontalPosition && _savedSetup.PickVerticalPosition == setup.PickVerticalPosition
        && _savedSetup.PlaceHorizontalPosition == setup.PlaceHorizontalPosition && _savedSetup.PlaceVerticalPosition == setup.PlaceVerticalPosition;

    private WaferHandlerDraft CaptureDraft() => new(HorizontalAxisId, VerticalAxisId, WorkpieceComponentId, SourcePresentSensorChannelId, GateOpenSensorChannelId, PickCommandChannelId, PlaceCommandChannelId, HoldingFeedbackChannelId, PlacedFeedbackChannelId, PickHorizontalText, PickVerticalText, PlaceHorizontalText, PlaceVerticalText);

    private void RestoreDraft(WaferHandlerDraft draft)
    {
        _horizontalAxisId = draft.HorizontalAxisId; _verticalAxisId = draft.VerticalAxisId; _workpieceComponentId = draft.WorkpieceComponentId;
        _sourcePresentSensorChannelId = draft.SourceInputId; _gateOpenSensorChannelId = draft.GateInputId; _pickCommandChannelId = draft.PickOutputId; _placeCommandChannelId = draft.PlaceOutputId;
        _holdingFeedbackChannelId = draft.HoldingInputId; _placedFeedbackChannelId = draft.PlacedInputId; _pickHorizontalText = draft.PickHorizontal; _pickVerticalText = draft.PickVertical;
        _placeHorizontalText = draft.PlaceHorizontal; _placeVerticalText = draft.PlaceVertical;
        RaiseValidationChanged();
    }

    private void SetSelection(ref string? field, string? value, string propertyName) { if (!IsDisposed && SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private void SetText(ref string field, string value, string propertyName) { if (!IsDisposed && SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private MachineLayoutDefinition? ActiveLayout() => _project is null ? null : ResolveActiveLayout(_project);
    private bool IsLinearAxis(string? id) => _project?.Axes.Any(axis => axis.Kind == AxisKind.Linear && Same(axis.Id, id)) == true;
    private bool IsLayoutComponent(string? id, LayoutComponentKind kind) => ActiveLayout()?.Components.Any(component => component.Kind == kind && Same(component.Id, id)) == true;
    private bool IsChannel(string? id, ChannelKind kind) => _project?.Channels.Any(channel => channel.Kind == kind && Same(channel.Id, id)) == true;
    private bool IsAxisPosition(string? axisId, string text) => TryGetAxis(axisId, out var axis) && TryFiniteDouble(text, out var value) && axis.SoftLimitMin.HasValue && axis.SoftLimitMax.HasValue && value >= axis.SoftLimitMin.Value && value <= axis.SoftLimitMax.Value;
    private bool TryGetAxis(string? id, out VirtualAxisDefinition axis) { axis = _project?.Axes.FirstOrDefault(candidate => Same(candidate.Id, id))!; return axis is not null; }
    private double AxisLimit(string? axisId, bool maximum) => TryGetAxis(axisId, out var axis) && axis.SoftLimitMin.HasValue && axis.SoftLimitMax.HasValue ? maximum ? axis.SoftLimitMax.Value : axis.SoftLimitMin.Value : 0;

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
        AxisOptions.Clear();
        WorkpieceOptions.Clear();
        InputOptions.Clear();
        OutputOptions.Clear();
        RaiseChanged();
        _previewCommand.RaiseCanExecuteChanged();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static bool Distinct(string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static bool TryFiniteDouble(string text, out double value) => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    private static double Parse(string text) => TryFiniteDouble(text, out var value) ? value : double.NaN;
    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private static LoadLockSetupOption Option(string id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}");
    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string? id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !Same(option.Id, id))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project) => !string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId) ? project.Layouts.FirstOrDefault(layout => Same(layout.Id, project.Simulation.ActiveLayoutId)) : project.Layouts.Count == 1 ? project.Layouts[0] : null;
    private static WaferHandlerDefinition Clone(WaferHandlerDefinition value) => new() { HorizontalAxisId = value.HorizontalAxisId, VerticalAxisId = value.VerticalAxisId, WorkpieceComponentId = value.WorkpieceComponentId, SourcePresentSensorChannelId = value.SourcePresentSensorChannelId, GateOpenSensorChannelId = value.GateOpenSensorChannelId, PickCommandChannelId = value.PickCommandChannelId, PlaceCommandChannelId = value.PlaceCommandChannelId, HoldingFeedbackChannelId = value.HoldingFeedbackChannelId, PlacedFeedbackChannelId = value.PlacedFeedbackChannelId, PickHorizontalPosition = value.PickHorizontalPosition, PickVerticalPosition = value.PickVerticalPosition, PlaceHorizontalPosition = value.PlaceHorizontalPosition, PlaceVerticalPosition = value.PlaceVerticalPosition };

    private sealed record WaferHandlerDraft(string? HorizontalAxisId, string? VerticalAxisId, string? WorkpieceComponentId, string? SourceInputId, string? GateInputId, string? PickOutputId, string? PlaceOutputId, string? HoldingInputId, string? PlacedInputId, string PickHorizontal, string PickVertical, string PlaceHorizontal, string PlaceVertical);
    private static readonly string[] ValidationProperties =
    [
        nameof(HorizontalAxisId), nameof(VerticalAxisId), nameof(WorkpieceComponentId), nameof(SourcePresentSensorChannelId), nameof(GateOpenSensorChannelId), nameof(PickCommandChannelId), nameof(PlaceCommandChannelId), nameof(HoldingFeedbackChannelId), nameof(PlacedFeedbackChannelId), nameof(PickHorizontalText), nameof(PickVerticalText), nameof(PlaceHorizontalText), nameof(PlaceVerticalText), nameof(IsHorizontalAxisValid), nameof(IsVerticalAxisValid), nameof(IsWorkpieceValid), nameof(IsSourcePresentValid), nameof(IsGateOpenValid), nameof(IsPickCommandValid), nameof(IsPlaceCommandValid), nameof(IsHoldingFeedbackValid), nameof(IsPlacedFeedbackValid), nameof(IsPickHorizontalValid), nameof(IsPickVerticalValid), nameof(IsPlaceHorizontalValid), nameof(IsPlaceVerticalValid), nameof(HasMultipleHandlers), nameof(HasValidationError), nameof(ValidationText)
    ];
}
