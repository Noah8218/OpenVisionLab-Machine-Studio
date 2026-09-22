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

public sealed class PrealignerSetupViewModel : ViewModelBase, IDisposable
{
    private readonly Func<PrealignerDefinition, int> _applySetup;
    private readonly Action _clearWorkbenchPreviews;
    private readonly RelayCommand _previewCommand;
    private readonly RelayCommand _applyCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _resetCommand;
    private MachineProjectDocument? _project;
    private bool _isEditable = true;
    private bool _isVisible;
    private PrealignerDefinition? _savedSetup;
    private string? _rotaryStageComponentId;
    private string? _clampCylinderComponentId;
    private string? _waferPresentSensorChannelId;
    private string? _alignmentAcceptedCommandChannelId;
    private string? _alignmentReadyFeedbackChannelId;
    private string? _alignmentCompleteFeedbackChannelId;
    private string _alignmentTargetText = string.Empty;
    private string _alignmentToleranceText = string.Empty;
    private int _disposed;

    public PrealignerSetupViewModel(Func<PrealignerDefinition, int> applySetup, Action clearWorkbenchPreviews)
    {
        _applySetup = applySetup;
        _clearWorkbenchPreviews = clearWorkbenchPreviews;
        _previewCommand = new RelayCommand(_ => Preview(), _ => !IsDisposed && IsEditable && _project is not null);
        _applyCommand = new RelayCommand(_ => Apply(), ignored => !IsDisposed && IsEditable && IsVisible && TryCreate(out _));
        _cancelCommand = new RelayCommand(_ => ClearPreviewForCompetingSetup(), _ => !IsDisposed && IsVisible);
        _resetCommand = new RelayCommand(_ => Reset(), _ => !IsDisposed && IsEditable && IsVisible);
    }

    public ObservableCollection<LoadLockSetupOption> StageOptions { get; } = new();
    public ObservableCollection<LoadLockSetupOption> CylinderOptions { get; } = new();
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
    public string? RotaryStageComponentId { get => _rotaryStageComponentId; set => SetSelection(ref _rotaryStageComponentId, value, nameof(RotaryStageComponentId)); }
    public string? ClampCylinderComponentId { get => _clampCylinderComponentId; set => SetSelection(ref _clampCylinderComponentId, value, nameof(ClampCylinderComponentId)); }
    public string? WaferPresentSensorChannelId { get => _waferPresentSensorChannelId; set => SetSelection(ref _waferPresentSensorChannelId, value, nameof(WaferPresentSensorChannelId)); }
    public string? AlignmentAcceptedCommandChannelId { get => _alignmentAcceptedCommandChannelId; set => SetSelection(ref _alignmentAcceptedCommandChannelId, value, nameof(AlignmentAcceptedCommandChannelId)); }
    public string? AlignmentReadyFeedbackChannelId { get => _alignmentReadyFeedbackChannelId; set => SetSelection(ref _alignmentReadyFeedbackChannelId, value, nameof(AlignmentReadyFeedbackChannelId)); }
    public string? AlignmentCompleteFeedbackChannelId { get => _alignmentCompleteFeedbackChannelId; set => SetSelection(ref _alignmentCompleteFeedbackChannelId, value, nameof(AlignmentCompleteFeedbackChannelId)); }
    public string AlignmentTargetText { get => _alignmentTargetText; set => SetText(ref _alignmentTargetText, value, nameof(AlignmentTargetText)); }
    public string AlignmentToleranceText { get => _alignmentToleranceText; set => SetText(ref _alignmentToleranceText, value, nameof(AlignmentToleranceText)); }
    public bool HasMultiplePrealigners => _project?.Devices.Count(device => device.Kind == DeviceKind.Prealigner) > 1;
    public bool IsRotaryStageValid => TryGetPrealignerStage(RotaryStageComponentId, out _);
    public bool IsClampCylinderValid => IsLayoutComponent(ClampCylinderComponentId, LayoutComponentKind.PneumaticCylinder);
    public bool IsWaferPresentValid => IsChannel(WaferPresentSensorChannelId, ChannelKind.DigitalInput);
    public bool IsAlignmentAcceptedValid => IsChannel(AlignmentAcceptedCommandChannelId, ChannelKind.DigitalOutput);
    public bool IsAlignmentReadyValid => IsChannel(AlignmentReadyFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsAlignmentCompleteValid => IsChannel(AlignmentCompleteFeedbackChannelId, ChannelKind.DigitalInput);
    public bool IsAlignmentTargetValid => IsRotaryPosition(RotaryStageComponentId, AlignmentTargetText);
    public bool IsAlignmentToleranceValid => TryPositiveDouble(AlignmentToleranceText, out _);
    public bool HasValidationError => !TryCreate(out _);
    public string ValidationText => OpenVisionLanguageService.T(
        HasMultiplePrealigners ? "Connections.PrealignerSetupMultipleError" : HasValidationError
            ? "Connections.PrealignerSetupValidationError"
            : "Connections.PrealignerSetupValidationReady");

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
        StageOptions.Clear(); CylinderOptions.Clear(); InputOptions.Clear(); OutputOptions.Clear();
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
        StageOptions.Clear(); CylinderOptions.Clear(); InputOptions.Clear(); OutputOptions.Clear();
        var layout = ResolveActiveLayout(_project);
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.RotaryStage).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal))
            StageOptions.Add(Option(component.Id, component.Name));
        foreach (var component in (layout?.Components ?? []).Where(component => component.Kind == LayoutComponentKind.PneumaticCylinder).OrderBy(component => component.Name, StringComparer.CurrentCulture).ThenBy(component => component.Id, StringComparer.Ordinal))
            CylinderOptions.Add(Option(component.Id, component.Name));
        foreach (var channel in _project.Channels.OrderBy(channel => channel.Name, StringComparer.CurrentCulture).ThenBy(channel => channel.Id, StringComparer.Ordinal))
        {
            var option = Option(channel.Id, channel.Name);
            if (channel.Kind == ChannelKind.DigitalInput) InputOptions.Add(option);
            if (channel.Kind == ChannelKind.DigitalOutput) OutputOptions.Add(option);
        }
        var existing = _project.Devices.Where(device => device is { Kind: DeviceKind.Prealigner, Prealigner: not null }).ToArray();
        _savedSetup = existing.Length == 1 ? Clone(existing[0].Prealigner!) : null;
        var draft = _savedSetup ?? Suggest();
        AddMissing(StageOptions, draft.RotaryStageComponentId); AddMissing(CylinderOptions, draft.ClampCylinderComponentId);
        foreach (var id in new[] { draft.WaferPresentSensorChannelId, draft.AlignmentReadyFeedbackChannelId, draft.AlignmentCompleteFeedbackChannelId }) AddMissing(InputOptions, id);
        AddMissing(OutputOptions, draft.AlignmentAcceptedCommandChannelId);
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

    private PrealignerDefinition Suggest() => new()
    {
        RotaryStageComponentId = StageOptions.FirstOrDefault()?.Id ?? string.Empty,
        ClampCylinderComponentId = CylinderOptions.FirstOrDefault()?.Id ?? string.Empty,
        WaferPresentSensorChannelId = InputOptions.ElementAtOrDefault(0)?.Id ?? string.Empty,
        AlignmentReadyFeedbackChannelId = InputOptions.ElementAtOrDefault(1)?.Id ?? string.Empty,
        AlignmentCompleteFeedbackChannelId = InputOptions.ElementAtOrDefault(2)?.Id ?? string.Empty,
        AlignmentAcceptedCommandChannelId = OutputOptions.FirstOrDefault()?.Id ?? string.Empty,
        AlignmentTargetDegrees = StageAxisLimit(StageOptions.FirstOrDefault()?.Id),
        AlignmentToleranceDegrees = 0.1
    };

    private void ApplyDraft(PrealignerDefinition setup)
    {
        _rotaryStageComponentId = setup.RotaryStageComponentId; _clampCylinderComponentId = setup.ClampCylinderComponentId; _waferPresentSensorChannelId = setup.WaferPresentSensorChannelId;
        _alignmentAcceptedCommandChannelId = setup.AlignmentAcceptedCommandChannelId; _alignmentReadyFeedbackChannelId = setup.AlignmentReadyFeedbackChannelId; _alignmentCompleteFeedbackChannelId = setup.AlignmentCompleteFeedbackChannelId;
        _alignmentTargetText = Format(setup.AlignmentTargetDegrees); _alignmentToleranceText = Format(setup.AlignmentToleranceDegrees);
        RaiseValidationChanged();
    }

    private bool TryCreate(out PrealignerDefinition setup)
    {
        var channels = new[] { WaferPresentSensorChannelId, AlignmentAcceptedCommandChannelId, AlignmentReadyFeedbackChannelId, AlignmentCompleteFeedbackChannelId };
        setup = new PrealignerDefinition
        {
            RotaryStageComponentId = RotaryStageComponentId ?? string.Empty, ClampCylinderComponentId = ClampCylinderComponentId ?? string.Empty,
            WaferPresentSensorChannelId = WaferPresentSensorChannelId ?? string.Empty, AlignmentAcceptedCommandChannelId = AlignmentAcceptedCommandChannelId ?? string.Empty,
            AlignmentReadyFeedbackChannelId = AlignmentReadyFeedbackChannelId ?? string.Empty, AlignmentCompleteFeedbackChannelId = AlignmentCompleteFeedbackChannelId ?? string.Empty,
            AlignmentTargetDegrees = Parse(AlignmentTargetText), AlignmentToleranceDegrees = Parse(AlignmentToleranceText)
        };
        return !HasMultiplePrealigners && IsRotaryStageValid && IsClampCylinderValid && Distinct(channels)
            && IsWaferPresentValid && IsAlignmentAcceptedValid && IsAlignmentReadyValid && IsAlignmentCompleteValid && IsAlignmentTargetValid && IsAlignmentToleranceValid;
    }

    private bool IsEquivalentToSaved(PrealignerDefinition setup) => _savedSetup is not null
        && Same(_savedSetup.RotaryStageComponentId, setup.RotaryStageComponentId) && Same(_savedSetup.ClampCylinderComponentId, setup.ClampCylinderComponentId)
        && Same(_savedSetup.WaferPresentSensorChannelId, setup.WaferPresentSensorChannelId) && Same(_savedSetup.AlignmentAcceptedCommandChannelId, setup.AlignmentAcceptedCommandChannelId)
        && Same(_savedSetup.AlignmentReadyFeedbackChannelId, setup.AlignmentReadyFeedbackChannelId) && Same(_savedSetup.AlignmentCompleteFeedbackChannelId, setup.AlignmentCompleteFeedbackChannelId)
        && _savedSetup.AlignmentTargetDegrees == setup.AlignmentTargetDegrees && _savedSetup.AlignmentToleranceDegrees == setup.AlignmentToleranceDegrees;

    private PrealignerDraft CaptureDraft() => new(RotaryStageComponentId, ClampCylinderComponentId, WaferPresentSensorChannelId, AlignmentAcceptedCommandChannelId, AlignmentReadyFeedbackChannelId, AlignmentCompleteFeedbackChannelId, AlignmentTargetText, AlignmentToleranceText);

    private void RestoreDraft(PrealignerDraft draft)
    {
        _rotaryStageComponentId = draft.StageId; _clampCylinderComponentId = draft.ClampId; _waferPresentSensorChannelId = draft.WaferPresentId; _alignmentAcceptedCommandChannelId = draft.AcceptedId;
        _alignmentReadyFeedbackChannelId = draft.ReadyId; _alignmentCompleteFeedbackChannelId = draft.CompleteId; _alignmentTargetText = draft.Target; _alignmentToleranceText = draft.Tolerance;
        RaiseValidationChanged();
    }

    private void SetSelection(ref string? field, string? value, string propertyName) { if (!IsDisposed && SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private void SetText(ref string field, string value, string propertyName) { if (!IsDisposed && SetProperty(ref field, value, propertyName)) RaiseValidationChanged(); }
    private MachineLayoutDefinition? ActiveLayout() => _project is null ? null : ResolveActiveLayout(_project);
    private bool IsLayoutComponent(string? id, LayoutComponentKind kind) => ActiveLayout()?.Components.Any(component => component.Kind == kind && Same(component.Id, id)) == true;
    private bool IsChannel(string? id, ChannelKind kind) => _project?.Channels.Any(channel => channel.Kind == kind && Same(channel.Id, id)) == true;
    private bool IsRotaryPosition(string? stageId, string text) => TryGetPrealignerStage(stageId, out var stage) && TryFiniteDouble(text, out var value) && _project!.Axes.FirstOrDefault(axis => Same(axis.Id, stage.BehaviorBindingId)) is { Kind: AxisKind.Rotary, SoftLimitMin: not null, SoftLimitMax: not null } axis && value >= axis.SoftLimitMin.Value && value <= axis.SoftLimitMax.Value;
    private bool TryGetPrealignerStage(string? id, out LayoutComponentDefinition stage) { var value = ActiveLayout()?.Components.FirstOrDefault(component => component.Kind == LayoutComponentKind.RotaryStage && Same(component.Id, id)); stage = value!; return value is not null && _project!.Axes.Any(axis => axis.Kind == AxisKind.Rotary && Same(axis.Id, value.BehaviorBindingId)); }
    private double StageAxisLimit(string? stageId) => TryGetPrealignerStage(stageId, out var stage) && _project!.Axes.FirstOrDefault(axis => Same(axis.Id, stage.BehaviorBindingId)) is { SoftLimitMin: not null } axis ? axis.SoftLimitMin.Value : 0;

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
        StageOptions.Clear();
        CylinderOptions.Clear();
        InputOptions.Clear();
        OutputOptions.Clear();
        RaiseChanged();
        _previewCommand.RaiseCanExecuteChanged();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static bool Distinct(string?[] values) => values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static bool TryPositiveDouble(string text, out double value) => TryFiniteDouble(text, out value) && value > 0;
    private static bool TryFiniteDouble(string text, out double value) => (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value);
    private static double Parse(string text) => TryFiniteDouble(text, out var value) ? value : double.NaN;
    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private static LoadLockSetupOption Option(string id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}");
    private static void AddMissing(ObservableCollection<LoadLockSetupOption> options, string? id) { if (!string.IsNullOrWhiteSpace(id) && options.All(option => !Same(option.Id, id))) options.Add(new LoadLockSetupOption(id, $"{id} ({OpenVisionLanguageService.T("Connections.LoadLockSetupMissing")})")); }
    private static MachineLayoutDefinition? ResolveActiveLayout(MachineProjectDocument project) => !string.IsNullOrWhiteSpace(project.Simulation.ActiveLayoutId) ? project.Layouts.FirstOrDefault(layout => Same(layout.Id, project.Simulation.ActiveLayoutId)) : project.Layouts.Count == 1 ? project.Layouts[0] : null;
    private static PrealignerDefinition Clone(PrealignerDefinition value) => new() { RotaryStageComponentId = value.RotaryStageComponentId, ClampCylinderComponentId = value.ClampCylinderComponentId, WaferPresentSensorChannelId = value.WaferPresentSensorChannelId, AlignmentAcceptedCommandChannelId = value.AlignmentAcceptedCommandChannelId, AlignmentReadyFeedbackChannelId = value.AlignmentReadyFeedbackChannelId, AlignmentCompleteFeedbackChannelId = value.AlignmentCompleteFeedbackChannelId, AlignmentTargetDegrees = value.AlignmentTargetDegrees, AlignmentToleranceDegrees = value.AlignmentToleranceDegrees };

    private sealed record PrealignerDraft(string? StageId, string? ClampId, string? WaferPresentId, string? AcceptedId, string? ReadyId, string? CompleteId, string Target, string Tolerance);
    private static readonly string[] ValidationProperties =
    [
        nameof(RotaryStageComponentId), nameof(ClampCylinderComponentId), nameof(WaferPresentSensorChannelId), nameof(AlignmentAcceptedCommandChannelId), nameof(AlignmentReadyFeedbackChannelId), nameof(AlignmentCompleteFeedbackChannelId), nameof(AlignmentTargetText), nameof(AlignmentToleranceText), nameof(IsRotaryStageValid), nameof(IsClampCylinderValid), nameof(IsWaferPresentValid), nameof(IsAlignmentAcceptedValid), nameof(IsAlignmentReadyValid), nameof(IsAlignmentCompleteValid), nameof(IsAlignmentTargetValid), nameof(IsAlignmentToleranceValid), nameof(HasMultiplePrealigners), nameof(HasValidationError), nameof(ValidationText)
    ];
}
