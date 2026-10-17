using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class CaptureParameterEditor : ViewModelBase
{
    private readonly string _name;
    private string _value;
    public CaptureParameterEditor(string name, string value, string unit) { _name = name; _value = value; Unit = unit; }
    public string Name => OpenVisionLanguageService.T(_name);
    public string Unit { get; }
    public string Value { get => _value; set => SetProperty(ref _value, value ?? ""); }
    public override string ToString() => Name;
    internal void RefreshLocalization() => OnPropertyChanged(nameof(Name));
}

/// <summary>Owns camera-scoped capture drafts; Apply changes the recipe without starting acquisition.</summary>
public sealed class CameraCaptureEditorViewModel : ViewModelBase
{
    private const string AppliedNeedsSave = "Camera.Capture.AppliedNeedsSave";
    private readonly Func<bool> _canEdit;
    private readonly Action<CameraImageSourceApplicationResult> _applied;
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
    private MachineProjectDocument _project = new();
    private string? _cameraId;
    private Draft _draft = new();
    private string _statusKey = "Camera.Capture.IndependentHint";
    private object[] _statusArguments = [];

    internal CameraCaptureEditorViewModel(Func<bool> canEdit, Action<CameraImageSourceApplicationResult> applied)
    {
        _canEdit = canEdit;
        _applied = applied;
        ApplyCommand = new RelayCommand(_ => Apply(), _ => CanEdit);
        ResetCommand = new RelayCommand(_ => { _draft = CreateDraft(new NumericalCameraSettings()); SetStatus("Camera.Capture.DefaultsLoaded"); NotifyDraft(); }, _ => CanEdit);
        RevertCommand = new RelayCommand(_ => { _drafts.Remove(_cameraId ?? ""); SelectCamera(_cameraId, true); }, _ => CanEdit);
        ConfirmVersionCommand = new RelayCommand(_ => ConfirmVersion(), _ => CanEdit);
    }

    public bool CanEdit => _canEdit() && Definition is not null;
    public bool Enabled { get => _draft.Enabled; set { _draft.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCaptureSettingsToApply)); } }
    public bool HasCaptureSettingsToApply => Enabled || Definition?.Camera?.NumericalInput is not null;
    public IReadOnlyList<NumericalCameraInputKind> InputKinds { get; } = Enum.GetValues<NumericalCameraInputKind>();
    public NumericalCameraInputKind InputKind { get => _draft.Kind; set { _draft.Kind = value; _draft.ConfirmedVersion = 0; OnPropertyChanged(); OnPropertyChanged(nameof(ConfirmationText)); } }
    public string RecipeName { get => _draft.Recipe; set { _draft.Recipe = value ?? ""; _draft.ConfirmedVersion = 0; OnPropertyChanged(); OnPropertyChanged(nameof(ConfirmationText)); } }
    public string Version { get => _draft.Version; set { _draft.Version = value ?? ""; _draft.ConfirmedVersion = 0; OnPropertyChanged(); OnPropertyChanged(nameof(ConfirmationText)); } }
    public bool Defect { get => _draft.Defect; set { _draft.Defect = value; OnPropertyChanged(); } }
    public IReadOnlyList<SequenceAuthoringTarget> Axes => new[] { new SequenceAuthoringTarget("", OpenVisionLanguageService.T("Camera.Capture.NoReferenceAxis"), SequenceAuthoringTargetKind.Axis) }
        .Concat(_project.Axes.Select(axis => new SequenceAuthoringTarget(axis.Id, axis.Name, SequenceAuthoringTargetKind.Axis))).ToArray();
    public string AxisId { get => _draft.AxisId; set { _draft.AxisId = value ?? ""; OnPropertyChanged(); } }
    public IReadOnlyList<CaptureParameterEditor> Parameters => _draft.Parameters;
    public IReadOnlyList<CaptureParameterEditor> VariationParameters => _draft.Parameters.Skip(1).Take(5).ToArray();
    public IReadOnlyList<CaptureParameterEditor> GenerationParameters => _draft.Parameters.Skip(6).Take(3).ToArray();
    public string ConfirmationText => _draft.ConfirmedVersion > 0 ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("Camera.Capture.VersionConfirmed"), _draft.ConfirmedVersion) : OpenVisionLanguageService.T("Camera.Capture.VersionRequired");
    public string Status => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T(_statusKey), _statusArguments);
    public bool HasError => _statusKey.StartsWith("Camera.Capture.Error.", StringComparison.Ordinal);
    public ICommand ApplyCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand ConfirmVersionCommand { get; }
    private DeviceDefinition? Definition => _project.Devices.FirstOrDefault(device => device.Id == _cameraId && device.Kind == DeviceKind.Camera);

    internal void Load(MachineProjectDocument project, string? cameraId)
    {
        _project = project;
        _drafts.Clear();
        SelectCamera(cameraId, true);
        OnPropertyChanged(nameof(Axes));
    }

    internal void NotifyProjectSaved()
    {
        if (_statusKey == AppliedNeedsSave) SetStatus("Camera.Capture.AppliedSaved");
    }

    internal void RefreshAuthoringTargets() => OnPropertyChanged(nameof(Axes));

    internal void RefreshLocalization()
    {
        foreach (var parameter in Parameters) parameter.RefreshLocalization();
        foreach (string property in new[] { nameof(Axes), nameof(ConfirmationText), nameof(Status) }) OnPropertyChanged(property);
    }

    internal void SelectCamera(string? cameraId, bool reload = false)
    {
        if (!reload && _cameraId == cameraId) return;
        if (!reload && _cameraId is not null) _drafts[_cameraId] = _draft;
        _cameraId = cameraId;
        _draft = cameraId is not null && _drafts.TryGetValue(cameraId, out var existing) ? existing : CreateDraft(Definition?.Camera?.NumericalInput);
        SetStatus("Camera.Capture.Loaded");
        NotifyDraft();
    }

    private void ConfirmVersion()
    {
        if (!CanEdit) return;
        if (!int.TryParse(Version, NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version is < 1 or > 100000) { SetStatus("Camera.Capture.Error.Version"); return; }
        _draft.ConfirmedVersion = version;
        OnPropertyChanged(nameof(ConfirmationText));
        SetStatus("Camera.Capture.Confirmed", RecipeName, version);
    }

    private void Apply()
    {
        if (!CanEdit || Definition is not { } definition) return;
        if (!Enabled)
        {
            (definition.Camera ??= new()).NumericalInput = null;
            Applied(definition.Id);
            return;
        }
        var values = new double[Parameters.Count];
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.TryParse(Parameters[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
            { SetStatus("Camera.Capture.Error.Finite", Parameters[i]); return; }
        }
        if (!int.TryParse(Version, out int version) || values[7] < 0 || values[7] > int.MaxValue || Math.Truncate(values[7]) != values[7]
            || values[8] is < 0 or > 60 || values[9] is < 0 or > 60 || values[8] * 1000 != Math.Truncate(values[8] * 1000) || values[9] * 1000 != Math.Truncate(values[9] * 1000))
        { SetStatus("Camera.Capture.Error.Timing"); return; }
        var settings = new NumericalCameraSettings
        {
            InputKind = InputKind, RecipeName = RecipeName.Trim(), Version = version, ConfirmedVersion = _draft.ConfirmedVersion, Defect = Defect,
            FieldOfView = values[0], OffsetX = values[1], OffsetY = values[2], Yaw = values[3], Tilt = values[4], Shake = values[5], Light = values[6], Seed = (int)values[7],
            GenerationDelayMilliseconds = (int)(values[8] * 1000), ResponseDelayMilliseconds = (int)(values[9] * 1000), AxisId = string.IsNullOrEmpty(AxisId) ? null : AxisId
        };
        if (settings.Validate() is { } error)
        {
            SetStatus(error switch
            {
                "2D 또는 3D 입력 종류를 선택하세요." => "Camera.Capture.Error.InputKind",
                "레시피 이름과 버전을 확인하세요." => "Camera.Capture.Error.Recipe",
                "시야는 10–10000 범위의 유한한 값이어야 합니다." => "Camera.Capture.Error.FieldOfView",
                "위치와 회전 편차를 확인하세요." => "Camera.Capture.Error.Position",
                "기울기와 흔들림 범위를 확인하세요." => "Camera.Capture.Error.Variation",
                "조명은 0–4, 시드는 0 이상의 정수여야 합니다." => "Camera.Capture.Error.LightSeed",
                "생성·응답 지연은 0–60000ms 범위여야 합니다." => "Camera.Capture.Error.Delay",
                _ => error
            });
            return;
        }
        if (settings.ConfirmedVersion != settings.Version) { SetStatus("Camera.Capture.Error.ConfirmVersion"); return; }
        if (settings.AxisId is { } axis && !_project.Axes.Any(candidate => candidate.Id == axis)) { SetStatus("Camera.Capture.Error.MissingAxis"); return; }
        int fixedStep = _project.Simulation.FixedStepMilliseconds;
        if (fixedStep > 0 && (settings.GenerationDelayMilliseconds % fixedStep != 0 || settings.ResponseDelayMilliseconds % fixedStep != 0))
        { SetStatus("Camera.Capture.Error.FixedStep", fixedStep); return; }
        var camera = definition.Camera ?? new VirtualCameraDefinition();
        long captureDelay = (long)camera.ExposureDelayMilliseconds + camera.TransferDelayMilliseconds + settings.GenerationDelayMilliseconds + settings.ResponseDelayMilliseconds;
        foreach (var sequence in _project.Sequences)
        foreach (var trigger in sequence.Steps.Where(step => step.Action == SequenceStepAction.TriggerCamera && step.TargetId == definition.Id))
        {
            var wait = sequence.Steps.FirstOrDefault(step => step.Id == trigger.NextStepId && step.Action == SequenceStepAction.WaitVisionResult && step.TargetId == definition.Id);
            if (wait is { TimeoutMs: > 0 } && captureDelay > wait.TimeoutMs)
            { SetStatus("Camera.Capture.Error.SequenceTimeout", captureDelay, sequence.Name, wait.Name, wait.TimeoutMs); return; }
        }
        (definition.Camera ??= new()).NumericalInput = settings;
        definition.Camera.SingleImageSource = null;
        foreach (var step in _project.Sequences.SelectMany(sequence => sequence.Steps).Where(step => step.Action == SequenceStepAction.TriggerCamera && step.TargetId == definition.Id))
            step.Parameter = settings.RecipeName;
        Applied(definition.Id);
    }

    private void Applied(string cameraId)
    {
        _drafts.Remove(cameraId);
        OnPropertyChanged(nameof(HasCaptureSettingsToApply));
        SetStatus(AppliedNeedsSave);
        _applied(new(CameraImageSourceApplicationOutcome.Applied, cameraId, "수치 모의 촬영 설정"));
    }

    private void SetStatus(string key, params object[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasError));
    }

    private void NotifyDraft()
    {
        foreach (string property in new[] { nameof(CanEdit), nameof(Enabled), nameof(HasCaptureSettingsToApply), nameof(InputKind), nameof(RecipeName), nameof(Version), nameof(Defect), nameof(AxisId), nameof(Parameters), nameof(VariationParameters), nameof(GenerationParameters), nameof(ConfirmationText) }) OnPropertyChanged(property);
        CommandManager.InvalidateRequerySuggested();
    }

    private static Draft CreateDraft(NumericalCameraSettings? saved)
    {
        var settings = saved ?? new NumericalCameraSettings();
        string[] names = ["Camera.Capture.Parameter.FieldOfView", "Camera.Capture.Parameter.OffsetX", "Camera.Capture.Parameter.OffsetY", "Camera.Capture.Parameter.Yaw", "Camera.Capture.Parameter.Tilt", "Camera.Capture.Parameter.Shake", "Camera.Capture.Parameter.Light", "Camera.Capture.Parameter.Seed", "Camera.Capture.Parameter.GenerationDelay", "Camera.Capture.Parameter.ResponseDelay"];
        string[] units = ["LU", "LU", "LU", "°", "°", "LU", "배", "", "초", "초"];
        double[] values = [settings.FieldOfView, settings.OffsetX, settings.OffsetY, settings.Yaw, settings.Tilt, settings.Shake, settings.Light, settings.Seed, settings.GenerationDelayMilliseconds / 1000d, settings.ResponseDelayMilliseconds / 1000d];
        return new Draft { Enabled = saved is not null, Kind = settings.InputKind, Recipe = settings.RecipeName, Version = settings.Version.ToString(CultureInfo.InvariantCulture),
            ConfirmedVersion = settings.ConfirmedVersion, Defect = settings.Defect, AxisId = settings.AxisId ?? "", Parameters = names.Select((name, i) => new CaptureParameterEditor(name, values[i].ToString("G", CultureInfo.InvariantCulture), units[i])).ToArray() };
    }

    private sealed class Draft
    {
        public bool Enabled;
        public NumericalCameraInputKind Kind;
        public string Recipe = "", Version = "1", AxisId = "";
        public int ConfirmedVersion;
        public bool Defect;
        public CaptureParameterEditor[] Parameters = [];
    }
}
