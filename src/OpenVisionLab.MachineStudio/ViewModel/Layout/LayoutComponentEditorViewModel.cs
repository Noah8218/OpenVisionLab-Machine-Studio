using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record LayoutPropertyOption(string Id, string DisplayName, bool IsEnabled = true);

internal readonly record struct LayoutPlacementDraft(
    string Name,
    double PositionX,
    double PositionDepth,
    double BaseElevation,
    double RotationDegrees,
    double FootprintWidth,
    double FootprintDepth,
    double VerticalHeight,
    bool ApplyVerticalHeight,
    string? UnitId = null,
    bool ApplyUnitAssignment = false);

/// <summary>
/// Adapts one selected authored component and its explicit behavior binding for
/// the Design inspector. Runtime state remains owned by the simulation engine.
/// </summary>
public sealed class LayoutComponentEditorViewModel : ViewModelBase, IDisposable
{
    private readonly MachineProjectDocument _project;
    private readonly LayoutItem _item;
    private readonly LayoutComponentDefinition _component;
    private readonly Action _definitionChanged;
    private readonly Func<bool> _canEditDefinition;
    private bool _disposed;
    private readonly Func<LayoutItem, LayoutPlacementDraft, bool> _applyPlacementDraft;
    private readonly Func<LayoutItem, VirtualAxisDefinition, double, double, double, double, bool> _applyAxisDriveDraft;
    private string _draftName = string.Empty;
    private string _draftXText = string.Empty;
    private string _draftDepthText = string.Empty;
    private string _draftBaseElevationText = string.Empty;
    private string _draftRotationText = string.Empty;
    private string _draftFootprintWidthText = string.Empty;
    private string _draftFootprintDepthText = string.Empty;
    private string _draftVerticalHeightText = string.Empty;
    private string _placementDraftError = string.Empty;
    private string _baselineName = string.Empty;
    private double _baselineX;
    private double _baselineDepth;
    private double _baselineBaseElevation;
    private double _baselineRotationDegrees;
    private double _baselineFootprintWidth;
    private double _baselineFootprintDepth;
    private double _baselineVerticalHeight;
    private bool _baselineHasExplicitVerticalEnvelope;
    private bool _hasValidationErrors;
    private string _validationMessage = string.Empty;
    private readonly RelayCommand _applyPlacementDraftCommand;
    private readonly RelayCommand _discardPlacementDraftCommand;
    private VirtualAxisDefinition? _axis;
    private readonly RelayCommand _applyAxisDriveDraftCommand;
    private readonly RelayCommand _discardAxisDriveDraftCommand;
    private double _baselineAxisMin;
    private double _baselineAxisMax;
    private double _baselineAxisHome;
    private double _baselineAxisSpeed;
    private string _draftAxisMinText = string.Empty;
    private string _draftAxisMaxText = string.Empty;
    private string _draftAxisHomeText = string.Empty;
    private string _draftAxisSpeedText = string.Empty;
    private string _axisDriveDraftError = string.Empty;
    private readonly Func<Action, bool>? _applySettingsDraft;
    private readonly RelayCommand _applySettingsDraftCommand;
    private readonly RelayCommand _applyEquipmentPlacementDraftCommand;
    private readonly RelayCommand _discardSettingsDraftCommand;
    private IReadOnlyList<EquipmentInspectorFieldRow> _settingsRows = [];
    private string _settingsBaseline = string.Empty;
    private string _settingsDraftError = string.Empty;
    private readonly Dictionary<string, string> _placementErrors = new(StringComparer.Ordinal);
    private string _draftUnitId = string.Empty;
    private string _baselineUnitId = string.Empty;

    internal LayoutComponentEditorViewModel(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutItem item,
        Action definitionChanged,
        Func<bool> canEditDefinition,
        Func<LayoutItem, LayoutPlacementDraft, bool> applyPlacementDraft,
        Func<LayoutItem, VirtualAxisDefinition, double, double, double, double, bool> applyAxisDriveDraft,
        Func<Action, bool>? applySettingsDraft = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(layout);
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _component = item.Component ?? throw new ArgumentException(
            "A layout component is required.",
            nameof(item));
        _definitionChanged = definitionChanged ?? throw new ArgumentNullException(nameof(definitionChanged));
        _canEditDefinition = canEditDefinition ?? throw new ArgumentNullException(nameof(canEditDefinition));
        _applyPlacementDraft = applyPlacementDraft ?? throw new ArgumentNullException(nameof(applyPlacementDraft));
        _applyAxisDriveDraft = applyAxisDriveDraft ?? throw new ArgumentNullException(nameof(applyAxisDriveDraft));
        _applySettingsDraft = applySettingsDraft;
        _axis = _component.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage
            ? project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, _component.BehaviorBindingId, StringComparison.Ordinal))
            : null;

        BehaviorBindingOptions = BuildBehaviorBindingOptions(project, layout, _component);
        DigitalInputOptions = BuildChannelOptions(project, ChannelKind.DigitalInput);
        DigitalOutputOptions = BuildChannelOptions(project, ChannelKind.DigitalOutput);
        TargetComponentOptions = layout.Components
            .Where(component => !string.Equals(component.Id, _component.Id, StringComparison.Ordinal))
            .Select(ToOption)
            .ToArray();
        ConveyorComponentOptions = layout.Components
            .Where(component => component.Kind == LayoutComponentKind.Conveyor)
            .Select(ToOption)
            .ToArray();
        UnitOptions = BuildUnitOptions(project);

        DiscardPlacementDraft();
        _applyPlacementDraftCommand = new RelayCommand(_ => TryApplyPlacementDraft(), _ => HasPendingPlacementDraft || HasPlacementValidationErrors, useCommandManagerRequery: false);
        _discardPlacementDraftCommand = new RelayCommand(_ => DiscardPlacementDraft(), _ => HasPendingPlacementDraft || HasPlacementValidationErrors, useCommandManagerRequery: false);
        _applyAxisDriveDraftCommand = new RelayCommand(_ => TryApplyAxisDriveDraft(), _ => HasPendingAxisDriveDraft, useCommandManagerRequery: false);
        _discardAxisDriveDraftCommand = new RelayCommand(_ => DiscardAxisDriveDraft(), _ => HasPendingAxisDriveDraft, useCommandManagerRequery: false);
        DiscardAxisDriveDraft();
        _applyEquipmentPlacementDraftCommand = new RelayCommand(_ => TryApplyInspectorDraft(), _ => !_disposed && _canEditDefinition());
        _applySettingsDraftCommand = new RelayCommand(_ => TryApplySettingsDraft(), _ => !_disposed && _canEditDefinition(), useCommandManagerRequery: true);
        _discardSettingsDraftCommand = new RelayCommand(_ => DiscardSettingsDraft(), _ => HasPendingSettingsDraft || SettingsDraftError.Length > 0, useCommandManagerRequery: false);
        DiscardSettingsDraft();
        _item.PropertyChanged += OnItemPropertyChanged;
        Validate();
    }

    public string Id => _component.Id;
    public string KindText => OpenVisionLanguageService.T(
        $"Properties.Value.{_component.Kind}",
        _component.Kind.ToString(),
        _component.Kind.ToString());
    public string PaneTitle => string.IsNullOrWhiteSpace(Name) ? KindText : Name;
    private bool IsHeightMapCamera => _component.Kind == LayoutComponentKind.Camera &&
        _project.Devices.FirstOrDefault(device => device.Id == BehaviorBindingId)?.Camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D;
    public string InspectorKindText => OpenVisionLanguageService.T(_component.Kind switch
    {
        LayoutComponentKind.Camera => IsHeightMapCamera ? "Equipment.OutlineCamera3D" : "Equipment.OutlineCamera2D",
        LayoutComponentKind.DigitalSensor => "Properties.Sensor",
        LayoutComponentKind.PneumaticCylinder => "Properties.Cylinder",
        _ => $"Properties.Value.{_component.Kind}"
    });
    public string InspectorSymbol => _component.Kind switch
    {
        LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage => "↔",
        LayoutComponentKind.Conveyor => "▭", LayoutComponentKind.DigitalSensor => "◉",
        LayoutComponentKind.Camera => IsHeightMapCamera ? "▤" : "▣",
        LayoutComponentKind.PneumaticCylinder => "⇨", LayoutComponentKind.DigitalIo => "◧",
        LayoutComponentKind.FeedPosition => "⇥", LayoutComponentKind.ExitPosition => "⇤",
        LayoutComponentKind.Workpiece => "◆", _ => "▦"
    };
    public string InspectorSymbolColor => _component.Kind switch
    {
        LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage => "#6DC5D6",
        LayoutComponentKind.Conveyor => "#7F9FAE", LayoutComponentKind.DigitalSensor => "#F0C76F",
        LayoutComponentKind.Camera => IsHeightMapCamera ? "#B48FF0" : "#92A4ED",
        LayoutComponentKind.PneumaticCylinder => "#70D9C5", LayoutComponentKind.DigitalIo => "#E4AA9D",
        LayoutComponentKind.FeedPosition => "#C9B46A", LayoutComponentKind.ExitPosition => "#D39A70", LayoutComponentKind.Workpiece => "#E4EDF2", _ => "#6FD8C4"
    };
    public string KindDescription => OpenVisionLanguageService.T(_component.Kind == LayoutComponentKind.Workpiece
        ? "R24.ProductDescription" : "R24.Description." + (_component.Kind == LayoutComponentKind.Camera ? IsHeightMapCamera ? "Camera3D" : "Camera2D" : _component.Kind.ToString()));
    public string InspectorPaneTitle => ShowWorkpieceProperties ? OpenVisionLanguageService.T("R24.ProductModel") : PaneTitle;
    public string ProductReference2DText => LinkedDevice?.Properties.GetValueOrDefault("reference2D") ?? OpenVisionLanguageService.T("R24.ReferenceUnspecified");
    public string ProductReference3DText => LinkedDevice?.Properties.GetValueOrDefault("reference3D") ?? OpenVisionLanguageService.T("R24.ReferenceUnspecified");

    public string Name
    {
        get => _item.CurrentName;
        set
        {
            if (!CanUpdateDefinition(nameof(Name))) return;
            if (string.Equals(_item.CurrentName, value, StringComparison.Ordinal))
            {
                return;
            }

            _item.CurrentName = value;
            Validate();
        }
    }

    public double X
    {
        get => _item.CurrentX;
        set { if (CanUpdateDefinition(nameof(X))) _item.SetCurrentX(value, snapToGrid: false); }
    }

    public double Y
    {
        get => _item.CurrentY;
        set { if (CanUpdateDefinition(nameof(Y))) _item.SetCurrentY(value, snapToGrid: false); }
    }

    public double RotationDegrees
    {
        get => _item.CurrentRotationDegrees;
        set { if (CanUpdateDefinition(nameof(RotationDegrees))) _item.CurrentRotationDegrees = value; }
    }

    public double Width
    {
        get => _item.CurrentWidth;
        set { if (CanUpdateDefinition(nameof(Width))) _item.CurrentWidth = value; }
    }

    public double Height
    {
        get => _item.CurrentHeight;
        set { if (CanUpdateDefinition(nameof(Height))) _item.CurrentHeight = value; }
    }

    public double VerticalBaseElevation
    {
        get => _item.CurrentVerticalBaseElevation;
        set { if (CanUpdateDefinition(nameof(VerticalBaseElevation))) _item.CurrentVerticalBaseElevation = value; }
    }

    public double VerticalHeight
    {
        get => _item.CurrentVerticalHeight;
        set { if (CanUpdateDefinition(nameof(VerticalHeight))) _item.CurrentVerticalHeight = value; }
    }

    public bool IsVerticalEnvelopeEstimated => !_item.HasExplicitVerticalEnvelope;

    public string? BehaviorBindingId
    {
        get => _item.CurrentBehaviorBindingId;
        set
        {
            if (!CanUpdateDefinition(nameof(BehaviorBindingId))) return;

            if (string.Equals(_item.CurrentBehaviorBindingId, value, StringComparison.Ordinal))
            {
                return;
            }

            if (HasPendingAxisDriveDraft)
            {
                AxisDriveDraftError = OpenVisionLanguageService.T("Equipment.AxisDriveDraftResolveFirst", "구동 변경을 적용하거나 버린 뒤 축 연결을 바꾸세요.", "Apply or discard drive changes before changing the axis binding.");
                OnPropertyChanged(nameof(BehaviorBindingId));
                return;
            }

            _item.CurrentBehaviorBindingId = value;
            _axis = _component.Kind is LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage
                ? _project.Axes.FirstOrDefault(axis => string.Equals(axis.Id, value, StringComparison.Ordinal))
                : null;
            DiscardAxisDriveDraft();
            OnPropertyChanged(string.Empty);
            Validate();
        }
    }

    public string UnitId
    {
        get => _item.CurrentUnitId ?? string.Empty;
        set { if (CanUpdateDefinition(nameof(UnitId))) _item.CurrentUnitId = value; }
    }

    public string DraftUnitId
    {
        get => _draftUnitId;
        set { if (SetProperty(ref _draftUnitId, value ?? string.Empty)) OnPlacementDraftChanged("unit"); }
    }

    public bool ShowDigitalIoProperties => _component.Kind == LayoutComponentKind.DigitalIo;
    public bool ShowFeedPositionProperties => _component.Kind == LayoutComponentKind.FeedPosition;
    public bool ShowNoManualCommand => _component.Kind is not (LayoutComponentKind.LinearStage or LayoutComponentKind.RotaryStage or LayoutComponentKind.PneumaticCylinder or LayoutComponentKind.Conveyor or LayoutComponentKind.DigitalSensor or LayoutComponentKind.Camera or LayoutComponentKind.DigitalIo or LayoutComponentKind.FeedPosition);
    public bool ShowCameraProperties => _component.Kind == LayoutComponentKind.Camera;
    public bool IsCameraMounted => LinkedDevice?.Camera?.Geometry?.AxisId is not null;

    public string DraftName
    {
        get => _draftName;
        set { if (SetProperty(ref _draftName, value ?? string.Empty)) OnPlacementDraftChanged("name"); }
    }

    public string DraftXText
    {
        get => _draftXText;
        set { if (SetProperty(ref _draftXText, value ?? string.Empty)) OnPlacementDraftChanged("x"); }
    }

    public string DraftDepthText
    {
        get => _draftDepthText;
        set { if (SetProperty(ref _draftDepthText, value ?? string.Empty)) OnPlacementDraftChanged("y"); }
    }

    public string DraftBaseElevationText
    {
        get => _draftBaseElevationText;
        set { if (SetProperty(ref _draftBaseElevationText, value ?? string.Empty)) OnPlacementDraftChanged("z"); }
    }

    public string DraftRotationText
    {
        get => _draftRotationText;
        set { if (SetProperty(ref _draftRotationText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftFootprintWidthText
    {
        get => _draftFootprintWidthText;
        set { if (SetProperty(ref _draftFootprintWidthText, value ?? string.Empty)) OnPlacementDraftChanged("length"); }
    }

    public string DraftFootprintDepthText
    {
        get => _draftFootprintDepthText;
        set { if (SetProperty(ref _draftFootprintDepthText, value ?? string.Empty)) OnPlacementDraftChanged("depth"); }
    }

    public string DraftVerticalHeightText
    {
        get => _draftVerticalHeightText;
        set { if (SetProperty(ref _draftVerticalHeightText, value ?? string.Empty)) OnPlacementDraftChanged("height"); }
    }

    public string DraftEquipmentXText
    {
        get => _component.Kind == LayoutComponentKind.Conveyor && double.TryParse(DraftXText, NumberStyles.Float, CultureInfo.InvariantCulture, out var center)
            ? FormatCoordinate(center - _baselineFootprintWidth / 2) : DraftXText;
        set
        {
            DraftXText = _component.Kind == LayoutComponentKind.Conveyor && double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var start)
                && double.IsFinite(start) ? FormatCoordinate(start + _baselineFootprintWidth / 2) : value;
            OnPropertyChanged(nameof(DraftEquipmentXText));
        }
    }

    public string DraftInstallationHeightText
    {
        get => double.TryParse(DraftBaseElevationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var baseHeight)
            ? FormatCoordinate(baseHeight + _baselineVerticalHeight) : DraftBaseElevationText;
        set
        {
            DraftBaseElevationText = double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var top)
                && double.IsFinite(top) ? FormatCoordinate(top - _baselineVerticalHeight) : value;
            OnPropertyChanged(nameof(DraftInstallationHeightText));
        }
    }

    public bool HasPendingPlacementDraft =>
        DraftUnitId != _baselineUnitId ||
        !string.Equals(DraftName, _baselineName, StringComparison.Ordinal) ||
        !string.Equals(DraftXText, FormatCoordinate(_baselineX), StringComparison.Ordinal) ||
        !string.Equals(DraftDepthText, FormatCoordinate(_baselineDepth), StringComparison.Ordinal) ||
        !string.Equals(DraftBaseElevationText, FormatCoordinate(_baselineBaseElevation), StringComparison.Ordinal) ||
        !string.Equals(DraftRotationText, FormatCoordinate(_baselineRotationDegrees), StringComparison.Ordinal) ||
        !string.Equals(DraftFootprintWidthText, FormatCoordinate(_baselineFootprintWidth), StringComparison.Ordinal) ||
        !string.Equals(DraftFootprintDepthText, FormatCoordinate(_baselineFootprintDepth), StringComparison.Ordinal) ||
        !string.Equals(DraftVerticalHeightText, FormatDraftVerticalHeight(_baselineVerticalHeight), StringComparison.Ordinal);

    public ICommand ApplyPlacementDraftCommand => _applyPlacementDraftCommand;
    public ICommand DiscardPlacementDraftCommand => _discardPlacementDraftCommand;

    public string PlacementDraftError
    {
        get => _placementDraftError;
        private set => SetProperty(ref _placementDraftError, value);
    }

    public bool TryApplyPlacementDraft()
    {
        if (!HasPendingPlacementDraft)
        {
            return true;
        }

        if (!TryValidatePlacementDraft(out var draft)) return false;

        if (!_applyPlacementDraft(_item, draft))
        {
            PlacementDraftError = OpenVisionLanguageService.T(
                "Equipment.PlacementDraftUnavailable",
                "현재 부품을 편집할 수 없습니다. 선택과 편집 모드를 확인하세요.",
                "This component cannot be edited now. Check the selection and editing mode.");
            return false;
        }

        DiscardPlacementDraft();
        return true;
    }

    private bool TryValidatePlacementDraft(out LayoutPlacementDraft draft)
    {
        draft = default;
        var name = DraftName.Trim();
        if (!UnitOptions.Any(option => option.Id == DraftUnitId) || name.Length is < 1 or > 80 ||
            !TryParseCoordinate(DraftXText, out var x) ||
            !TryParseCoordinate(DraftDepthText, out var depth) ||
            !TryParseCoordinate(DraftBaseElevationText, out var baseElevation) ||
            !TryParseCoordinate(DraftRotationText, out var rotation) || rotation is < -360 or > 360 ||
            !TryParseDimension(DraftFootprintWidthText, out var footprintWidth) ||
            !TryParseDimension(DraftFootprintDepthText, out var footprintDepth) ||
            !TryParseDimension(DraftVerticalHeightText, out var verticalHeight))
        {
            PlacementDraftError = OpenVisionLanguageService.T(
                "Equipment.PlacementDraftInvalid",
                "이름은 1~80자, 회전은 -360~360°, 길이·폭·높이는 1~10000 LU로 입력하고 좌표를 확인하세요.",
                "Enter a 1–80 character name, valid coordinates, rotation from -360 to 360°, and width/depth/height from 1 to 10000 LU.");
            return false;
        }

        if (!_baselineHasExplicitVerticalEnvelope &&
            string.Equals(DraftVerticalHeightText, FormatDraftVerticalHeight(_baselineVerticalHeight), StringComparison.Ordinal))
        {
            verticalHeight = _baselineVerticalHeight;
        }

        if (_item.CurrentName != _baselineName || _item.CurrentX != _baselineX ||
            _item.CurrentY != _baselineDepth || _item.CurrentVerticalBaseElevation != _baselineBaseElevation ||
            _item.CurrentRotationDegrees != _baselineRotationDegrees || _item.CurrentWidth != _baselineFootprintWidth ||
            _item.CurrentHeight != _baselineFootprintDepth || _item.VerticalHeight != _baselineVerticalHeight ||
            _item.HasExplicitVerticalEnvelope != _baselineHasExplicitVerticalEnvelope || (_item.CurrentUnitId ?? string.Empty) != _baselineUnitId)
        {
            PlacementDraftError = OpenVisionLanguageService.T(
                "Equipment.PlacementDraftStale",
                "부품이 다른 작업에서 변경되었습니다. 초안을 버리고 현재 값을 다시 확인하세요.",
                "The component changed elsewhere. Discard the draft and review the current values.");
            return false;
        }

        draft = new LayoutPlacementDraft(
            name,
            x,
            depth,
            baseElevation,
            rotation,
            footprintWidth,
            footprintDepth,
            verticalHeight,
            _baselineHasExplicitVerticalEnvelope ||
                !string.Equals(DraftVerticalHeightText, FormatDraftVerticalHeight(_baselineVerticalHeight), StringComparison.Ordinal),
            string.IsNullOrEmpty(DraftUnitId) ? null : DraftUnitId,
            true);
        return true;
    }

    public void DiscardPlacementDraft()
    {
        _placementErrors.Clear();
        NotifyInspectorErrors();
        _baselineUnitId = _item.CurrentUnitId ?? string.Empty;
        DraftUnitId = _baselineUnitId;
        _baselineName = _item.CurrentName;
        _baselineX = _item.CurrentX;
        _baselineDepth = _item.CurrentY;
        _baselineBaseElevation = _item.CurrentVerticalBaseElevation;
        _baselineRotationDegrees = _item.CurrentRotationDegrees;
        _baselineFootprintWidth = _item.CurrentWidth;
        _baselineFootprintDepth = _item.CurrentHeight;
        _baselineVerticalHeight = _item.VerticalHeight;
        _baselineHasExplicitVerticalEnvelope = _item.HasExplicitVerticalEnvelope;
        DraftName = _baselineName;
        DraftXText = FormatCoordinate(_baselineX);
        DraftDepthText = FormatCoordinate(_baselineDepth);
        DraftBaseElevationText = FormatCoordinate(_baselineBaseElevation);
        DraftRotationText = FormatCoordinate(_baselineRotationDegrees);
        DraftFootprintWidthText = FormatCoordinate(_baselineFootprintWidth);
        DraftFootprintDepthText = FormatCoordinate(_baselineFootprintDepth);
        DraftVerticalHeightText = FormatDraftVerticalHeight(_baselineVerticalHeight);
        PlacementDraftError = string.Empty;
        OnPropertyChanged(nameof(DraftInstallationHeightText));
        OnPropertyChanged(nameof(DraftEquipmentXText));
        OnPropertyChanged(nameof(HasPendingPlacementDraft));
        OnPropertyChanged(nameof(HasPendingInspectorDraft));
        _applyPlacementDraftCommand?.RaiseCanExecuteChanged();
        _discardPlacementDraftCommand?.RaiseCanExecuteChanged();
    }

    public bool ShowAxisDriveProperties => _axis is not null;
    public string AxisUnit => _axis?.Unit ?? string.Empty;
    public string DraftAxisMinText { get => _draftAxisMinText; set { if (SetProperty(ref _draftAxisMinText, value ?? string.Empty)) OnAxisDriveDraftChanged(); } }
    public string DraftAxisMaxText { get => _draftAxisMaxText; set { if (SetProperty(ref _draftAxisMaxText, value ?? string.Empty)) OnAxisDriveDraftChanged(); } }
    public string DraftAxisHomeText { get => _draftAxisHomeText; set { if (SetProperty(ref _draftAxisHomeText, value ?? string.Empty)) OnAxisDriveDraftChanged(); } }
    public string DraftAxisSpeedText { get => _draftAxisSpeedText; set { if (SetProperty(ref _draftAxisSpeedText, value ?? string.Empty)) OnAxisDriveDraftChanged(); } }
    public string AxisDriveDraftError { get => _axisDriveDraftError; private set => SetProperty(ref _axisDriveDraftError, value); }
    public bool HasPendingAxisDriveDraft => _axis is not null &&
        (DraftAxisMinText != FormatCoordinate(_baselineAxisMin) || DraftAxisMaxText != FormatCoordinate(_baselineAxisMax) ||
         DraftAxisHomeText != FormatCoordinate(_baselineAxisHome) || DraftAxisSpeedText != FormatCoordinate(_baselineAxisSpeed));
    public bool HasPendingInspectorDraft => HasPendingPlacementDraft || HasPendingAxisDriveDraft || HasPendingSettingsDraft;
    public ICommand ApplyAxisDriveDraftCommand => _applyAxisDriveDraftCommand;
    public ICommand DiscardAxisDriveDraftCommand => _discardAxisDriveDraftCommand;

    public bool TryApplyInspectorDraft()
    {
        if (_disposed || !_canEditDefinition()) return false;
        var placementPending = HasPendingPlacementDraft;
        var axisPending = HasPendingAxisDriveDraft;
        var settingsPending = HasPendingSettingsDraft;
        if (!placementPending && !axisPending && !settingsPending) return true;
        LayoutPlacementDraft placement = default;
        double min = 0, max = 0, home = 0, speed = 0;
        Action? settingsUpdate = null;
        if (placementPending && (!TryValidateEquipmentPlacement() || !TryValidatePlacementDraft(out placement))) return false;
        if (axisPending && !TryValidateAxisDriveDraft(out min, out max, out home, out speed)) return false;
        if (settingsPending && !TryValidateSettingsDraft(out settingsUpdate)) return false;
        if (_applySettingsDraft is null)
            return TryApplyPlacementDraft() && TryApplyAxisDriveDraft();
        if (!_applySettingsDraft(() =>
        {
            if (placementPending)
            {
                _item.CurrentName = placement.Name;
                _item.CurrentUnitId = placement.UnitId;
                _item.SetCurrentX(placement.PositionX, snapToGrid: false);
                _item.SetCurrentY(placement.PositionDepth, snapToGrid: false);
                _item.CurrentRotationDegrees = placement.RotationDegrees;
                _item.CurrentWidth = placement.FootprintWidth;
                _item.CurrentHeight = placement.FootprintDepth;
                _item.CurrentVerticalBaseElevation = placement.BaseElevation;
                if (placement.ApplyVerticalHeight) _item.CurrentVerticalHeight = placement.VerticalHeight;
            }
            if (axisPending && _axis is not null)
            {
                _axis.SoftLimitMin = min;
                _axis.SoftLimitMax = max;
                _axis.HomePosition = home;
                _axis.MaxVelocity = speed;
            }
            settingsUpdate?.Invoke();
        })) return false;
        DiscardInspectorDraft();
        return true;
    }

    public string PlacementNameError => PlacementError("name");
    public string PlacementUnitError => PlacementError("unit");
    public string PlacementXError => PlacementError("x");
    public string PlacementYError => PlacementError("y");
    public string PlacementZError => PlacementError("z");
    public string PlacementLengthError => PlacementError("length");
    public string PlacementDepthError => PlacementError("depth");
    public string PlacementHeightError => PlacementError("height");
    public bool HasPlacementNameError => PlacementNameError.Length > 0;
    public bool HasPlacementUnitError => PlacementUnitError.Length > 0;
    public bool HasPlacementXError => PlacementXError.Length > 0;
    public bool HasPlacementYError => PlacementYError.Length > 0;
    public bool HasPlacementZError => PlacementZError.Length > 0;
    public bool HasPlacementLengthError => PlacementLengthError.Length > 0;
    public bool HasPlacementDepthError => PlacementDepthError.Length > 0;
    public bool HasPlacementHeightError => PlacementHeightError.Length > 0;
    public bool HasPlacementValidationErrors => _placementErrors.Count > 0;
    public string PlacementValidationSummary => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.ErrorCountFormat"), _placementErrors.Count);
    public bool HasSettingsValidationErrors => SettingsFields.Any(field => field.HasError);
    public string SettingsValidationSummary => string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.ErrorCountFormat"), SettingsFields.Count(field => field.HasError));
    private string PlacementError(string id) => _placementErrors.GetValueOrDefault(id) ?? string.Empty;
    private void NotifyInspectorErrors()
    {
        foreach (var property in new[] { nameof(PlacementNameError), nameof(PlacementUnitError), nameof(PlacementXError), nameof(PlacementYError), nameof(PlacementZError),
            nameof(PlacementLengthError), nameof(PlacementDepthError), nameof(PlacementHeightError), nameof(HasPlacementNameError), nameof(HasPlacementUnitError), nameof(HasPlacementXError), nameof(HasPlacementYError), nameof(HasPlacementZError), nameof(HasPlacementLengthError), nameof(HasPlacementDepthError), nameof(HasPlacementHeightError), nameof(HasPlacementValidationErrors), nameof(PlacementValidationSummary), nameof(HasSettingsValidationErrors), nameof(SettingsValidationSummary) }) OnPropertyChanged(property);
    }
    private static string NumberError(string text, double minimum, double maximum, bool integer = false, string unit = "")
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value)) return OpenVisionLanguageService.T("R24.NumberRequired");
        if (integer && value != Math.Truncate(value)) return OpenVisionLanguageService.T("R24.IntegerRequired");
        return value < minimum || value > maximum ? string.Format(CultureInfo.CurrentCulture, OpenVisionLanguageService.T("R24.RangeRequired"), minimum, maximum, unit.Length == 0 ? "" : " " + unit) : string.Empty;
    }
    private bool TryValidateEquipmentPlacement()
    {
        _placementErrors.Clear();
        var nameLength = DraftName.Trim().Length;
        if (nameLength == 0) _placementErrors["name"] = OpenVisionLanguageService.T("R24.NameRequired");
        else if (nameLength > 60) _placementErrors["name"] = OpenVisionLanguageService.T("R24.NameTooLong");
        if (!ShowWorkpieceProperties && !UnitOptions.Any(option => option.Id == DraftUnitId)) _placementErrors["unit"] = OpenVisionLanguageService.T("R24.UnitRequired");
        void Check(string id, string text, double minimum, double maximum)
        {
            var error = NumberError(text, minimum, maximum, unit: "mm");
            if (error.Length > 0) _placementErrors[id] = error;
        }
        if (ShowWorkpieceProperties)
        {
            Check("length", DraftFootprintWidthText, 10, 1000); Check("depth", DraftFootprintDepthText, 10, 1000); Check("height", DraftVerticalHeightText, 1, 500);
        }
        else
        {
            if (ShowEquipmentPlacementX) Check("x", DraftEquipmentXText, -5000, 5000);
            Check("y", DraftDepthText, -5000, 5000);
            if (ShowCameraProperties) Check("z", DraftInstallationHeightText, 50, 2000);
        }
        PlacementDraftError = _placementErrors.Values.FirstOrDefault() ?? string.Empty;
        NotifyInspectorErrors();
        return _placementErrors.Count == 0;
    }

    public void DiscardInspectorDraft()
    {
        DiscardPlacementDraft();
        DiscardAxisDriveDraft();
        DiscardSettingsDraft();
    }

    public bool TryApplyAxisDriveDraft()
    {
        if (!HasPendingAxisDriveDraft) return true;
        if (!TryValidateAxisDriveDraft(out var min, out var max, out var home, out var speed)) return false;

        if (!_applyAxisDriveDraft(_item, _axis!, min, max, home, speed))
        {
            AxisDriveDraftError = OpenVisionLanguageService.T("Equipment.PlacementDraftUnavailable", "현재 부품을 편집할 수 없습니다. 선택과 편집 모드를 확인하세요.", "This component cannot be edited now. Check the selection and editing mode.");
            return false;
        }

        DiscardAxisDriveDraft();
        return true;
    }

    private bool TryValidateAxisDriveDraft(out double min, out double max, out double home, out double speed)
    {
        min = max = home = speed = 0;
        if (_axis is null) return false;
        if (!TryParseAxisValue(DraftAxisMinText, out min) || !TryParseAxisValue(DraftAxisMaxText, out max) ||
            !TryParseAxisValue(DraftAxisHomeText, out home) || !TryParseAxisValue(DraftAxisSpeedText, out speed) ||
            min >= max || home < min || home > max || speed <= 0)
        {
            AxisDriveDraftError = OpenVisionLanguageService.T("Equipment.AxisDriveDraftInvalid", "최소·최대 범위, 원점과 양수 속도를 확인하세요.", "Check the travel range, home position, and positive speed.");
            return false;
        }

        if ((_axis.SoftLimitMin ?? 0) != _baselineAxisMin || (_axis.SoftLimitMax ?? 300) != _baselineAxisMax ||
            _axis.HomePosition != _baselineAxisHome || _axis.MaxVelocity != _baselineAxisSpeed)
        {
            AxisDriveDraftError = OpenVisionLanguageService.T("Equipment.AxisDriveDraftStale", "축이 다른 작업에서 변경되었습니다. 초안을 버리고 현재 값을 다시 확인하세요.", "The axis changed elsewhere. Discard the draft and review the current values.");
            return false;
        }

        return true;
    }

    public void DiscardAxisDriveDraft()
    {
        if (_axis is null)
        {
            OnPropertyChanged(nameof(ShowAxisDriveProperties));
            OnPropertyChanged(nameof(HasPendingAxisDriveDraft));
            OnPropertyChanged(nameof(HasPendingInspectorDraft));
            return;
        }
        _baselineAxisMin = _axis.SoftLimitMin ?? 0;
        _baselineAxisMax = _axis.SoftLimitMax ?? 300;
        _baselineAxisHome = _axis.HomePosition;
        _baselineAxisSpeed = _axis.MaxVelocity;
        DraftAxisMinText = FormatCoordinate(_baselineAxisMin);
        DraftAxisMaxText = FormatCoordinate(_baselineAxisMax);
        DraftAxisHomeText = FormatCoordinate(_baselineAxisHome);
        DraftAxisSpeedText = FormatCoordinate(_baselineAxisSpeed);
        AxisDriveDraftError = string.Empty;
        OnPropertyChanged(nameof(HasPendingAxisDriveDraft));
        OnPropertyChanged(nameof(HasPendingInspectorDraft));
        OnPropertyChanged(nameof(ShowAxisDriveProperties));
    }

    public IReadOnlyList<EquipmentInspectorFieldRow> SettingsRows => _settingsRows;
    public IEnumerable<EquipmentInspectorField> SettingsFields => _settingsRows.SelectMany(row => row.Second is null ? new[] { row.First } : new[] { row.First, row.Second });
    public bool HasPendingSettingsDraft => SettingsFields.Any(field => field.Value != field.Baseline);
    public string SettingsDraftError { get => _settingsDraftError; private set { if (SetProperty(ref _settingsDraftError, value)) _discardSettingsDraftCommand?.RaiseCanExecuteChanged(); } }
    public ICommand ApplySettingsDraftCommand => _applySettingsDraftCommand;
    public ICommand DiscardSettingsDraftCommand => _discardSettingsDraftCommand;
    public ICommand ApplyEquipmentPlacementDraftCommand => _applyEquipmentPlacementDraftCommand;
    public string SettingsHint => _component.Kind is LayoutComponentKind.Camera or LayoutComponentKind.DigitalSensor or LayoutComponentKind.FeedPosition or LayoutComponentKind.Workpiece or LayoutComponentKind.MachineFrame
        ? string.Empty : OpenVisionLanguageService.T("R24.SettingsHint." + _component.Kind);
    public bool HasSettingsHint => SettingsHint.Length > 0;
    public bool IsCameraAxisMounted => ShowCameraProperties && (LinkedDevice?.Camera?.Geometry is { } mountGeometry ? mountGeometry.AxisId : LinkedDevice?.Camera?.NumericalInput?.AxisId) is { Length: > 0 };
    public bool ShowEquipmentPlacementX => !IsCameraAxisMounted;
    public string SettingsDetails => _component.Kind switch
    {
        LayoutComponentKind.Conveyor => OpenVisionLanguageService.T("R24.RunOutput") + "    " + ChannelName(Conveyor?.RunCommandChannelId),
        LayoutComponentKind.DigitalSensor => OpenVisionLanguageService.T("R24.SensorOutput") + "    " + ChannelName(Sensor?.OutputChannelId),
        LayoutComponentKind.PneumaticCylinder => OpenVisionLanguageService.T("R24.ExtendCommand") + "    " + ChannelName(Cylinder?.ExtendCommandChannelId) + "\n" + OpenVisionLanguageService.T("R24.EndSensors") + "    " + ChannelName(Cylinder?.ExtendedSensorChannelId) + ", " + ChannelName(Cylinder?.RetractedSensorChannelId),
        LayoutComponentKind.DigitalIo => OpenVisionLanguageService.T("R24.Output") + "    " + ChannelName(_component.BehaviorBindingId) + "\n" + OpenVisionLanguageService.T("R24.Input") + "    " + ChannelName(_project.Channels.FirstOrDefault(channel => channel.Kind == ChannelKind.DigitalInput && channel.Id == _component.Id + ".in")?.Id),
        LayoutComponentKind.Camera => CameraGeometryDetails,
        _ => string.Empty
    };
    public IReadOnlyList<EquipmentInspectorDetail> SettingsDetailRows
    {
        get
        {
            EquipmentInspectorDetail Row(string key, string value) => new(OpenVisionLanguageService.T(key), value);
            if (_component.Kind == LayoutComponentKind.Camera)
            {
                var camera = LinkedDevice?.Camera;
                var geometry = camera?.Geometry;
                var fov = camera?.NumericalInput?.FieldOfView ?? 120;
                var width = geometry?.ResolutionWidth ?? camera?.SingleImageSource?.Width ?? 128;
                var height = geometry?.ResolutionHeight ?? camera?.SingleImageSource?.Height ?? 128;
                var pixelX = (geometry?.FieldOfViewWidth ?? fov) / Math.Max(1, width);
                var pixelY = (geometry?.FieldOfViewHeight ?? fov) / Math.Max(1, height);
                return [new EquipmentInspectorDetail(OpenVisionLanguageService.T("R24.Resolution"), $"{pixelX:0.00} × {pixelY:0.00} mm/px", Math.Abs(pixelX - pixelY) > .001 ? " " + OpenVisionLanguageService.T("R24.NonSquareResolution") : string.Empty),
                    Row("R24.ImageFormat", OpenVisionLanguageService.T(IsHeightMapCamera ? "R24.HeightFormat" : "R24.MonoFormat")),
                    Row("R24.CameraDirectionCaption", OpenVisionLanguageService.T("R24.CameraDirection"))];
            }
            return _component.Kind switch
            {
                LayoutComponentKind.Conveyor => [Row("R24.RunOutput", ChannelName(Conveyor?.RunCommandChannelId))],
                LayoutComponentKind.DigitalSensor => [Row("R24.SensorOutput", ChannelName(Sensor?.OutputChannelId))],
                LayoutComponentKind.PneumaticCylinder => [Row("R24.ExtendCommand", ChannelName(Cylinder?.ExtendCommandChannelId)), Row("R24.EndSensors", ChannelName(Cylinder?.ExtendedSensorChannelId) + ", " + ChannelName(Cylinder?.RetractedSensorChannelId))],
                LayoutComponentKind.DigitalIo => [Row("R24.Output", ChannelName(_component.BehaviorBindingId)), Row("R24.Input", ChannelName(_project.Channels.FirstOrDefault(channel => channel.Kind == ChannelKind.DigitalInput && channel.Id == _component.Id + ".in")?.Id))],
                _ => []
            };
        }
    }

    public string CameraGeometryDetails
    {
        get
        {
            var camera = LinkedDevice?.Camera;
            var geometry = camera?.Geometry;
            var fov = camera?.NumericalInput?.FieldOfView ?? 120;
            var width = geometry?.ResolutionWidth ?? camera?.SingleImageSource?.Width ?? 128;
            var height = geometry?.ResolutionHeight ?? camera?.SingleImageSource?.Height ?? 128;
            return OpenVisionLanguageService.T("R24.Resolution") + $"    {(geometry?.FieldOfViewWidth ?? fov) / Math.Max(1, width):0.00} × {(geometry?.FieldOfViewHeight ?? fov) / Math.Max(1, height):0.00} mm/px\n" +
                OpenVisionLanguageService.T("R24.ImageFormat") + "    " + OpenVisionLanguageService.T(camera?.NumericalInput?.InputKind == NumericalCameraInputKind.HeightMap3D ? "R24.HeightFormat" : "R24.MonoFormat") + "\n" + OpenVisionLanguageService.T("R24.CameraDirection");
        }
    }

    public bool TryApplySettingsDraft()
    {
        if (_disposed || !_canEditDefinition()) return false;
        if (!HasPendingSettingsDraft) { SettingsDraftError = string.Empty; return true; }
        if (!TryValidateSettingsDraft(out var update)) return false;
        if (_applySettingsDraft is null || !_applySettingsDraft(update!))
        {
            SettingsDraftError = OpenVisionLanguageService.T("Equipment.PlacementDraftUnavailable");
            return false;
        }
        DiscardSettingsDraft();
        DiscardAxisDriveDraft();
        DiscardPlacementDraft();
        OnPropertyChanged(nameof(SettingsDetails));
        OnPropertyChanged(nameof(SettingsDetailRows));
        return true;
    }

    private string CaptureSettingsBaseline() => JsonSerializer.Serialize(new { Component = _component, Device = LinkedDevice, Axis = _axis });

    private bool TryValidateSettingsDraft(out Action? update)
    {
        update = null;
        if (CaptureSettingsBaseline() != _settingsBaseline)
        {
            SettingsDraftError = OpenVisionLanguageService.T("Equipment.AxisDriveDraftStale");
            return false;
        }
        var fields = SettingsFields.ToDictionary(field => field.Id, field => field.Value);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var field in SettingsFields) field.SetError(string.Empty);
        foreach (var field in SettingsFields.Where(field => field.IsNumeric))
        {
            var error = NumberError(field.Value, field.Minimum, field.Maximum, field.IsInteger);
            field.SetError(error);
            if (error.Length == 0) values[field.Id] = double.Parse(field.Value, CultureInfo.CurrentCulture);
        }
        NotifyInspectorErrors();
        if (HasSettingsValidationErrors)
        {
            SettingsDraftError = SettingsFields.First(field => field.HasError).ErrorText;
            return false;
        }
        double V(string id) => values[id];
        switch (_component.Kind)
        {
            case LayoutComponentKind.LinearStage:
            case LayoutComponentKind.RotaryStage:
                if (_axis is null) return InvalidSettings();
                var teaching = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var entry in fields["teach"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || !TryParseAxisValue(pair[1], out var position)) return InvalidSettings("teach", "R24.TeachingFormatError");
                    if (position < V("min") || position > V("max")) return InvalidSettings("teach", "R24.TeachingRangeError");
                    teaching[pair[0]] = position;
                }
                if (V("min") >= V("max")) return InvalidSettings("max", "R24.LimitOrderError");
                if (V("home") < V("min") || V("home") > V("max")) return InvalidSettings("home", "R24.HomeRangeError");
                update = () => { _axis.SoftLimitMin = V("min"); _axis.SoftLimitMax = V("max"); _axis.HomePosition = V("home"); _axis.MaxVelocity = V("speed"); _axis.TeachingPositions = teaching; };
                break;
            case LayoutComponentKind.Conveyor:
                if (Conveyor is null) return InvalidSettings();
                update = () =>
                {
                    var start = _item.CurrentX - _item.CurrentWidth / 2;
                    _item.CurrentWidth = V("length");
                    _item.CurrentHeight = V("width");
                    _item.SetCurrentX(start + V("length") / 2, snapToGrid: false);
                    Conveyor.SpeedUnitsPerSecond = V("speed");
                };
                break;
            case LayoutComponentKind.DigitalSensor:
                if (Sensor is null || fields["target"] != "all" && !TargetComponentOptions.Any(option => option.Id == fields["target"])) return InvalidSettings();
                update = () => { _item.CurrentWidth = V("zoneW"); _item.CurrentHeight = V("zoneD"); Sensor.DetectionCenterY = V("zoneZ"); Sensor.OnDelayMilliseconds = (int)V("onDelay"); Sensor.OffDelayMilliseconds = (int)V("offDelay"); Sensor.UseMillisecondDelays = true; Sensor.DetectAllProducts = fields["target"] == "all"; if (fields["target"] != "all") Sensor.TargetComponentId = fields["target"]; };
                break;
            case LayoutComponentKind.PneumaticCylinder:
                if (Cylinder is null) return InvalidSettings();
                var duration = Math.Max(1, (int)Math.Round(V("stroke") / V("speed") * 1000 / Math.Max(1, _project.Simulation.FixedStepMilliseconds))) * Math.Max(1, _project.Simulation.FixedStepMilliseconds);
                if (duration > 600000) return InvalidSettings();
                update = () => { Cylinder.Stroke = V("stroke"); Cylinder.ExtendDurationMilliseconds = duration; Cylinder.RetractDurationMilliseconds = duration; };
                break;
            case LayoutComponentKind.Camera:
                if (LinkedDevice?.Camera is not { } camera) return InvalidSettings();
                var axisId = fields["mount"];
                if (axisId.Length > 0 && !_project.Axes.Any(axis => axis.Id == axisId)) return InvalidSettings();
                var geometry = new CameraGeometryDefinition(V("fovW"), V("fovH"), (int)V("resW"), (int)V("resH"), axisId.Length == 0 ? null : axisId, values.GetValueOrDefault("offX"), V("lookZ"));
                update = () =>
                {
                    var previousMount = camera.Geometry is { } previousGeometry ? previousGeometry.AxisId : camera.NumericalInput?.AxisId;
                    if (previousMount is not null && axisId.Length == 0)
                    {
                        var mount = _project.Layouts.SelectMany(layout => layout.Components).FirstOrDefault(component => component.Kind == LayoutComponentKind.LinearStage && component.BehaviorBindingId == previousMount);
                        var axis = _project.Axes.FirstOrDefault(axis => axis.Id == previousMount);
                        if (mount is not null && axis is not null) _item.SetCurrentX(mount.Transform.X + axis.HomePosition + (camera.Geometry?.OffsetX ?? 0), snapToGrid: false);
                    }
                    camera.Geometry = geometry;
                };
                break;
            case LayoutComponentKind.FeedPosition:
                if (!ConveyorComponentOptions.Any(option => option.Id == fields["conveyor"])) return InvalidSettings("conveyor", "R24.ConveyorRequired");
                update = () => { _component.MaterialConveyorId = fields["conveyor"]; _item.CurrentWidth = V("zone"); };
                break;
            case LayoutComponentKind.ExitPosition:
                if (fields["route"] is not ("OK" or "NG")) return InvalidSettings();
                update = () => { _component.MaterialRoute = fields["route"]; _item.CurrentWidth = V("zone"); };
                break;
            default: update = () => { }; break;
        }
        SettingsDraftError = string.Empty;
        return true;
    }

    private bool InvalidSettings(string? fieldId = null, string key = "R24.InvalidSettings")
    {
        SettingsDraftError = OpenVisionLanguageService.T(key);
        if (fieldId is not null) SettingsFields.First(field => field.Id == fieldId).SetError(SettingsDraftError);
        NotifyInspectorErrors();
        return false;
    }

    public void DiscardSettingsDraft()
    {
        var fields = new List<EquipmentInspectorField>();
        void Number(string id, double value, double min = -1000000, double max = 1000000, bool integer = false) => fields.Add(new EquipmentInspectorField(id, _axis is null ? OpenVisionLanguageService.T(id == "zone" ? _component.Kind == LayoutComponentKind.FeedPosition ? "R24.FeedZone" : "R24.ExitZone" : "R24.Field." + id) : OpenVisionLanguageService.T("R24.Field." + id).Replace("mm", _axis.Unit, StringComparison.Ordinal), FormatCoordinate(value), OnSettingsDraftChanged, minimum: min, maximum: max, integer: integer));
        void Choice(string id, string value, IReadOnlyList<LayoutPropertyOption> options, string help = "") => fields.Add(new EquipmentInspectorField(id, OpenVisionLanguageService.T("R24.Field." + id), value, OnSettingsDraftChanged, options: options, help: help));
        switch (_component.Kind)
        {
            case LayoutComponentKind.LinearStage:
            case LayoutComponentKind.RotaryStage:
                if (_axis is not null)
                {
                    Number("min", _axis.SoftLimitMin ?? 0, -2000, 2000); Number("max", _axis.SoftLimitMax ?? 300, -2000, 2000); Number("home", _axis.HomePosition, -2000, 2000); Number("speed", _axis.MaxVelocity, 1, 3000);
                    fields.Add(new EquipmentInspectorField("teach", OpenVisionLanguageService.T("R24.Field.teach"), string.Join("; ", (_axis.TeachingPositions ?? []).Select(pair => pair.Key + "=" + FormatCoordinate(pair.Value))), OnSettingsDraftChanged, text: true, fullWidth: true));
                }
                break;
            case LayoutComponentKind.Conveyor: Number("length", _item.CurrentWidth, 100, 5000); Number("width", _item.CurrentHeight, 40, 1000); Number("speed", Conveyor?.SpeedUnitsPerSecond ?? 0, 1, 3000); break;
            case LayoutComponentKind.DigitalSensor:
                Choice("target", Sensor?.DetectAllProducts == true ? "all" : Sensor?.TargetComponentId ?? string.Empty,
                    new[] { new LayoutPropertyOption("all", OpenVisionLanguageService.T("R24.AllProducts")) }
                        .Concat(Sensor?.DetectAllProducts == true ? Array.Empty<LayoutPropertyOption>() : TargetComponentOptions.Where(option => option.Id == Sensor?.TargetComponentId))
                        .Append(new LayoutPropertyOption("future-part", OpenVisionLanguageService.T("R24.FutureSensorTarget"), false)).ToArray(), OpenVisionLanguageService.T("R24.SensorTargetHelp"));
                fields[^1].FullWidth = true;
                Number("zoneW", _item.CurrentWidth, 2, 500); Number("zoneD", _item.CurrentHeight, 2, 500); Number("zoneZ", Sensor?.DetectionCenterY ?? _item.CurrentY, -1000, 1000);
                Number("onDelay", Sensor?.OnDelayMilliseconds ?? 0, 0, 2000, true); Number("offDelay", Sensor?.OffDelayMilliseconds ?? 0, 0, 2000, true); break;
            case LayoutComponentKind.PneumaticCylinder: Number("stroke", Cylinder?.Stroke ?? 0, 10, 1000); Number("speed", (Cylinder?.Stroke ?? 0) / Math.Max(.001, (Cylinder?.ExtendDurationMilliseconds ?? 0) / 1000d), 1, 3000); break;
            case LayoutComponentKind.Camera:
                var camera = LinkedDevice?.Camera; var geometry = camera?.Geometry; var fov = camera?.NumericalInput?.FieldOfView ?? 120;
                Choice("mount", (geometry is not null ? geometry.AxisId : camera?.NumericalInput?.AxisId) ?? string.Empty, new[] { new LayoutPropertyOption(string.Empty, OpenVisionLanguageService.T("R24.FixedMount")) }.Concat(_project.Axes.Select(axis => new LayoutPropertyOption(axis.Id, axis.Name + OpenVisionLanguageService.T("R24.AxisMountSuffix")))).ToArray()); fields[^1].FullWidth = true;
                if (fields[^1].Value.Length > 0) Number("offX", geometry?.OffsetX ?? 0, -1000, 1000);
                Number("fovW", geometry?.FieldOfViewWidth ?? fov, 10, 2000); Number("fovH", geometry?.FieldOfViewHeight ?? fov, 10, 2000);
                Number("resW", geometry?.ResolutionWidth ?? camera?.SingleImageSource?.Width ?? 128, 16, 320, true); Number("resH", geometry?.ResolutionHeight ?? camera?.SingleImageSource?.Height ?? 128, 16, 320, true); Number("lookZ", geometry?.CenterY ?? 0, -1000, 1000); break;
            case LayoutComponentKind.FeedPosition: Choice("conveyor", _component.MaterialConveyorId ?? string.Empty, new[] { new LayoutPropertyOption(string.Empty, OpenVisionLanguageService.T("R24.Select")) }.Concat(ConveyorComponentOptions).ToArray()); fields[^1].FullWidth = true; Number("zone", _item.CurrentWidth, 10, 500); break;
            case LayoutComponentKind.ExitPosition: Choice("route", _component.MaterialRoute ?? "OK", [new("OK", "OK 경로"), new("NG", "NG 경로")]); Number("zone", _item.CurrentWidth, 10, 500); break;
        }
        var rows = new List<EquipmentInspectorFieldRow>();
        for (var i = 0; i < fields.Count; i++)
        {
            var first = fields[i];
            var second = !first.FullWidth && i + 1 < fields.Count && !fields[i + 1].FullWidth ? fields[++i] : null;
            rows.Add(new(first, second) { IsLast = i + 1 == fields.Count });
        }
        _settingsRows = rows;
        _settingsBaseline = CaptureSettingsBaseline();
        SettingsDraftError = string.Empty;
        OnPropertyChanged(nameof(SettingsRows));
        OnPropertyChanged(nameof(SettingsDetails));
        OnPropertyChanged(nameof(SettingsDetailRows));
        NotifyInspectorErrors();
        OnPropertyChanged(nameof(SettingsHint));
        OnPropertyChanged(nameof(HasSettingsHint));
        OnPropertyChanged(nameof(IsCameraAxisMounted));
        OnPropertyChanged(nameof(ShowEquipmentPlacementX));
        OnSettingsDraftChanged();
    }

    private void OnSettingsDraftChanged()
    {
        NotifyInspectorErrors();
        SettingsDraftError = string.Empty;
        OnPropertyChanged(nameof(HasPendingSettingsDraft));
        OnPropertyChanged(nameof(HasPendingInspectorDraft));
        _discardSettingsDraftCommand?.RaiseCanExecuteChanged();
    }

    private string ChannelName(string? id) => _project.Channels.FirstOrDefault(channel => channel.Id == id)?.Name ?? id ?? "—";

    public IReadOnlyList<LayoutPropertyOption> BehaviorBindingOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> DigitalInputOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> DigitalOutputOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> TargetComponentOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> ConveyorComponentOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> UnitOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> EquipmentUnitOptions => UnitOptions.Where(option => option.Id.Length > 0 || UnitOptions.Count == 1)
        .Select(option => new LayoutPropertyOption(option.Id, option.DisplayName.EndsWith(" / " + option.Id, StringComparison.Ordinal) ? option.DisplayName[..^(option.Id.Length + 3)] : option.DisplayName)).ToArray();
    public IReadOnlyList<LayoutPropertyOption> InspectionStateOptions =>
        Enum.GetValues<WorkpieceInspectionState>()
            .Select(state => new LayoutPropertyOption(
                state.ToString(),
                OpenVisionLanguageService.T(
                    $"Properties.Value.{state}",
                    state.ToString(),
                    state.ToString())))
            .ToArray();

    public bool HasBehaviorBinding => _component.Kind is not (LayoutComponentKind.MachineFrame or LayoutComponentKind.FeedPosition or LayoutComponentKind.ExitPosition);
    public bool HasUnitAssignmentOptions => UnitOptions.Count > 1;
    public bool ShowSensorProperties => _component.Kind == LayoutComponentKind.DigitalSensor && Sensor is not null;
    public bool ShowCylinderProperties => _component.Kind == LayoutComponentKind.PneumaticCylinder && Cylinder is not null;
    public bool ShowConveyorProperties => _component.Kind == LayoutComponentKind.Conveyor && Conveyor is not null;
    public bool ShowWorkpieceProperties => _component.Kind == LayoutComponentKind.Workpiece && Workpiece is not null;

    public string? SensorOutputChannelId
    {
        get => Sensor?.OutputChannelId;
        set => UpdateSensor(sensor => sensor.OutputChannelId = value ?? string.Empty, nameof(SensorOutputChannelId));
    }

    public string? SensorTargetComponentId
    {
        get => Sensor?.TargetComponentId;
        set => UpdateSensor(sensor => sensor.TargetComponentId = value ?? string.Empty, nameof(SensorTargetComponentId));
    }

    public double SensorOnDelayMilliseconds
    {
        get => Sensor?.OnDelayMilliseconds ?? 0;
        set => UpdateSensor(sensor => sensor.OnDelayMilliseconds = ConvertToInt(value), nameof(SensorOnDelayMilliseconds));
    }

    public double SensorOffDelayMilliseconds
    {
        get => Sensor?.OffDelayMilliseconds ?? 0;
        set => UpdateSensor(sensor => sensor.OffDelayMilliseconds = ConvertToInt(value), nameof(SensorOffDelayMilliseconds));
    }

    public string? CylinderExtendCommandChannelId
    {
        get => Cylinder?.ExtendCommandChannelId;
        set => UpdateCylinder(cylinder => cylinder.ExtendCommandChannelId = value ?? string.Empty, nameof(CylinderExtendCommandChannelId));
    }

    public string? CylinderExtendedSensorChannelId
    {
        get => Cylinder?.ExtendedSensorChannelId;
        set => UpdateCylinder(cylinder => cylinder.ExtendedSensorChannelId = value ?? string.Empty, nameof(CylinderExtendedSensorChannelId));
    }

    public string? CylinderRetractedSensorChannelId
    {
        get => Cylinder?.RetractedSensorChannelId;
        set => UpdateCylinder(cylinder => cylinder.RetractedSensorChannelId = value ?? string.Empty, nameof(CylinderRetractedSensorChannelId));
    }

    public double CylinderExtendDurationMilliseconds
    {
        get => Cylinder?.ExtendDurationMilliseconds ?? 0;
        set => UpdateCylinder(cylinder => cylinder.ExtendDurationMilliseconds = ConvertToInt(value), nameof(CylinderExtendDurationMilliseconds));
    }

    public double CylinderRetractDurationMilliseconds
    {
        get => Cylinder?.RetractDurationMilliseconds ?? 0;
        set => UpdateCylinder(cylinder => cylinder.RetractDurationMilliseconds = ConvertToInt(value), nameof(CylinderRetractDurationMilliseconds));
    }

    public double CylinderExtendedSensorDelayMilliseconds
    {
        get => Cylinder?.ExtendedSensorDelayMilliseconds ?? 0;
        set => UpdateCylinder(cylinder => cylinder.ExtendedSensorDelayMilliseconds = ConvertToInt(value), nameof(CylinderExtendedSensorDelayMilliseconds));
    }

    public double CylinderRetractedSensorDelayMilliseconds
    {
        get => Cylinder?.RetractedSensorDelayMilliseconds ?? 0;
        set => UpdateCylinder(cylinder => cylinder.RetractedSensorDelayMilliseconds = ConvertToInt(value), nameof(CylinderRetractedSensorDelayMilliseconds));
    }

    public double CylinderStroke
    {
        get => Cylinder?.Stroke ?? 0;
        set => UpdateCylinder(cylinder => cylinder.Stroke = value, nameof(CylinderStroke));
    }

    public string? ConveyorRunCommandChannelId
    {
        get => Conveyor?.RunCommandChannelId;
        set => UpdateConveyor(conveyor => conveyor.RunCommandChannelId = value ?? string.Empty, nameof(ConveyorRunCommandChannelId));
    }

    public string? ConveyorReverseCommandChannelId
    {
        get => Conveyor?.ReverseCommandChannelId;
        set => UpdateConveyor(conveyor => conveyor.ReverseCommandChannelId = value ?? string.Empty, nameof(ConveyorReverseCommandChannelId));
    }

    public double ConveyorSpeedUnitsPerSecond
    {
        get => Conveyor?.SpeedUnitsPerSecond ?? 0;
        set => UpdateConveyor(conveyor => conveyor.SpeedUnitsPerSecond = value, nameof(ConveyorSpeedUnitsPerSecond));
    }

    public string WorkpieceType
    {
        get => Workpiece?.Type ?? string.Empty;
        set => UpdateWorkpiece(workpiece => workpiece.Type = value, nameof(WorkpieceType));
    }

    public string? WorkpieceConveyorComponentId
    {
        get => Workpiece?.ConveyorComponentId;
        set => UpdateWorkpiece(workpiece => workpiece.ConveyorComponentId = value ?? string.Empty, nameof(WorkpieceConveyorComponentId));
    }

    public string? WorkpieceInspectionStateId
    {
        get => Workpiece?.InspectionState.ToString();
        set
        {
            if (Enum.TryParse(value, out WorkpieceInspectionState state))
            {
                UpdateWorkpiece(workpiece => workpiece.InspectionState = state, nameof(WorkpieceInspectionStateId));
            }
        }
    }

    public bool HasValidationErrors
    {
        get => _hasValidationErrors;
        private set => SetProperty(ref _hasValidationErrors, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(PaneTitle));
        OnPropertyChanged(nameof(KindDescription));
        OnPropertyChanged(nameof(InspectorKindText));
        OnPropertyChanged(nameof(InspectionStateOptions));
        Validate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _item.PropertyChanged -= OnItemPropertyChanged;
    }

    private DeviceDefinition? LinkedDevice => _project.Devices.FirstOrDefault(device =>
        string.Equals(device.Id, _component.BehaviorBindingId, StringComparison.Ordinal));
    private DigitalSensorDefinition? Sensor => LinkedDevice?.Sensor;
    private PneumaticCylinderDefinition? Cylinder => LinkedDevice?.Cylinder;
    private ConveyorDefinition? Conveyor => LinkedDevice?.Conveyor;
    private WorkpieceDefinition? Workpiece => LinkedDevice?.Workpiece;

    private void UpdateSensor(Action<DigitalSensorDefinition> update, string propertyName)
    {
        if (!CanUpdateDefinition(propertyName)) return;
        if (Sensor is { } sensor)
        {
            update(sensor);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private void UpdateCylinder(Action<PneumaticCylinderDefinition> update, string propertyName)
    {
        if (!CanUpdateDefinition(propertyName)) return;
        if (Cylinder is { } cylinder)
        {
            update(cylinder);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private void UpdateConveyor(Action<ConveyorDefinition> update, string propertyName)
    {
        if (!CanUpdateDefinition(propertyName)) return;
        if (Conveyor is { } conveyor)
        {
            update(conveyor);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private void UpdateWorkpiece(Action<WorkpieceDefinition> update, string propertyName)
    {
        if (!CanUpdateDefinition(propertyName)) return;
        if (Workpiece is { } workpiece)
        {
            update(workpiece);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private bool CanUpdateDefinition(string propertyName)
    {
        if (!_disposed && _canEditDefinition()) return true;
        OnPropertyChanged(propertyName);
        return false;
    }

    private void NotifyBehaviorChanged(string propertyName)
    {
        OnPropertyChanged(propertyName);
        _definitionChanged();
        Validate();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        string? editorProperty = args.PropertyName switch
        {
            nameof(LayoutItem.CurrentName) or nameof(LayoutItem.Name) => nameof(Name),
            nameof(LayoutItem.CurrentX) => nameof(X),
            nameof(LayoutItem.CurrentY) => nameof(Y),
            nameof(LayoutItem.CurrentRotationDegrees) or nameof(LayoutItem.RotationDegrees) => nameof(RotationDegrees),
            nameof(LayoutItem.CurrentWidth) or nameof(LayoutItem.Width) => nameof(Width),
            nameof(LayoutItem.CurrentHeight) or nameof(LayoutItem.Height) => nameof(Height),
            nameof(LayoutItem.VerticalBaseElevation) => nameof(VerticalBaseElevation),
            nameof(LayoutItem.VerticalHeight) => nameof(VerticalHeight),
            nameof(LayoutItem.HasExplicitVerticalEnvelope) => nameof(IsVerticalEnvelopeEstimated),
            nameof(LayoutItem.CurrentUnitId) or nameof(LayoutItem.UnitId) => nameof(UnitId),
            nameof(LayoutItem.CurrentBehaviorBindingId) or nameof(LayoutItem.BehaviorBindingId) => nameof(BehaviorBindingId),
            _ => null
        };
        if (editorProperty is not null)
        {
            OnPropertyChanged(editorProperty);
            if (editorProperty == nameof(Name)) OnPropertyChanged(nameof(PaneTitle));
            if (editorProperty == nameof(BehaviorBindingId))
            {
                OnPropertyChanged(nameof(InspectorKindText));
                OnPropertyChanged(nameof(InspectorSymbol));
                OnPropertyChanged(nameof(InspectorSymbolColor));
                OnPropertyChanged(nameof(KindDescription));
            }
            Validate();
            if (!HasPendingPlacementDraft)
            {
                DiscardPlacementDraft();
            }
        }
    }

    private void OnPlacementDraftChanged(string? fieldId = null)
    {
        if (fieldId is not null && _placementErrors.Remove(fieldId)) NotifyInspectorErrors();
        PlacementDraftError = _placementErrors.Values.FirstOrDefault() ?? string.Empty;
        OnPropertyChanged(nameof(HasPendingPlacementDraft));
        OnPropertyChanged(nameof(HasPendingInspectorDraft));
        _applyPlacementDraftCommand?.RaiseCanExecuteChanged();
        _discardPlacementDraftCommand?.RaiseCanExecuteChanged();
    }

    private void OnAxisDriveDraftChanged()
    {
        AxisDriveDraftError = string.Empty;
        OnPropertyChanged(nameof(HasPendingAxisDriveDraft));
        OnPropertyChanged(nameof(HasPendingInspectorDraft));
        _applyAxisDriveDraftCommand?.RaiseCanExecuteChanged();
        _discardAxisDriveDraftCommand?.RaiseCanExecuteChanged();
    }

    private static bool TryParseAxisValue(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value) && value is >= -1000000 and <= 1000000;

    private static string FormatCoordinate(double value) => value.ToString("G17", CultureInfo.CurrentCulture);

    private string FormatDraftVerticalHeight(double value) => _baselineHasExplicitVerticalEnvelope
        ? FormatCoordinate(value)
        : value.ToString("0.###", CultureInfo.CurrentCulture);

    private static bool TryParseCoordinate(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value) && value is >= -10000 and <= 10000;

    private static bool TryParseDimension(string text, out double value) =>
        TryParseCoordinate(text, out value) && value is >= 1 and <= 10000;

    private void Validate()
    {
        MachineProjectLayoutValidationError[] errors = new MachineProjectLayoutValidator()
            .Validate(_project)
            .Errors
            .Where(error => string.Equals(error.ComponentId, _component.Id, StringComparison.Ordinal))
            .ToArray();
        HasValidationErrors = errors.Length != 0;
        ValidationMessage = errors.Length == 0
            ? OpenVisionLanguageService.T(
                "Inspector.PropertiesValid",
                "작성 속성이 유효합니다.",
                "Authored properties are valid.")
            : OpenVisionLanguageService.T(
                "Inspector.PropertiesInvalid",
                "속성을 확인하세요.",
                "Check the authored properties.") + $" {LocalizeValidationMessage(errors[0])}" +
              (errors.Length > 1 ? $" (+{errors.Length - 1})" : string.Empty);
    }

    private static string LocalizeValidationMessage(MachineProjectLayoutValidationError error)
    {
        if (OpenVisionLanguageService.CurrentLanguage == OpenVisionLanguage.Korean &&
            error.Code is not (MachineProjectLayoutValidationErrorCode.LayoutNameRequired or MachineProjectLayoutValidationErrorCode.ComponentNameRequired))
        {
            var instruction = OpenVisionLanguageService.T($"LayoutValidation.{error.Code}", error.Message, error.Message);
            return string.Equals(instruction, error.Message, StringComparison.Ordinal) ? error.Message : $"{instruction} {error.Message}";
        }

        return error.Code switch
        {
            MachineProjectLayoutValidationErrorCode.LayoutNameRequired =>
                OpenVisionLanguageService.T(
                    "LayoutValidation.LayoutNameRequired",
                    "레이아웃 이름이 필요합니다.",
                    "Every layout requires a name."),
            MachineProjectLayoutValidationErrorCode.ComponentNameRequired =>
                OpenVisionLanguageService.T(
                    "LayoutValidation.ComponentNameRequired",
                    "모든 레이아웃 구성요소에 이름이 필요합니다.",
                    "Every layout component requires a name."),
            _ => error.Message
        };
    }

    private static int ConvertToInt(double value) => checked((int)Math.Round(value));

    private static IReadOnlyList<LayoutPropertyOption> BuildBehaviorBindingOptions(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutComponentDefinition component) => component.Kind switch
    {
        LayoutComponentKind.LinearStage => project.Axes
            .Where(axis => axis.Kind == AxisKind.Linear)
            .Select(axis => new LayoutPropertyOption(axis.Id, DisplayName(axis.Name, axis.Id)))
            .ToArray(),
        LayoutComponentKind.RotaryStage => project.Axes
            .Where(axis => axis.Kind == AxisKind.Rotary)
            .Select(axis => new LayoutPropertyOption(axis.Id, DisplayName(axis.Name, axis.Id)))
            .ToArray(),
        LayoutComponentKind.DigitalIo => project.Channels.Where(channel => channel.Kind is ChannelKind.DigitalInput or ChannelKind.DigitalOutput).Select(channel => new LayoutPropertyOption(channel.Id, channel.Name)).ToArray(),
        LayoutComponentKind.DigitalSensor => BuildDeviceOptions(project, DeviceKind.Sensor),
        LayoutComponentKind.PneumaticCylinder => BuildDeviceOptions(project, DeviceKind.Cylinder),
        LayoutComponentKind.Conveyor => BuildDeviceOptions(project, DeviceKind.Conveyor),
        LayoutComponentKind.Workpiece => BuildDeviceOptions(project, DeviceKind.Workpiece),
        LayoutComponentKind.Camera => BuildCameraOptions(project, layout, component.Id),
        _ => Array.Empty<LayoutPropertyOption>()
    };

    private static IReadOnlyList<LayoutPropertyOption> BuildCameraOptions(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        string componentId)
    {
        var usedCameraIds = layout.Components
            .Where(candidate => candidate.Kind == LayoutComponentKind.Camera && candidate.Id != componentId)
            .Select(candidate => candidate.BehaviorBindingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        return project.Devices
            .Where(device => device.Kind == DeviceKind.Camera && !usedCameraIds.Contains(device.Id))
            .Select(device => new LayoutPropertyOption(device.Id, DisplayName(device.Name, device.Id)))
            .ToArray();
    }

    private static IReadOnlyList<LayoutPropertyOption> BuildDeviceOptions(
        MachineProjectDocument project,
        DeviceKind kind) => project.Devices
        .Where(device => device.Kind == kind)
        .Select(device => new LayoutPropertyOption(device.Id, DisplayName(device.Name, device.Id)))
        .ToArray();

    private static IReadOnlyList<LayoutPropertyOption> BuildChannelOptions(
        MachineProjectDocument project,
        ChannelKind kind) => project.Channels
        .Where(channel => channel.Kind == kind)
        .Select(channel => new LayoutPropertyOption(channel.Id, DisplayName(channel.Name, channel.Id)))
        .ToArray();

    private static IReadOnlyList<LayoutPropertyOption> BuildUnitOptions(MachineProjectDocument project)
    {
        var options = new List<LayoutPropertyOption>
        {
            new(string.Empty, OpenVisionLanguageService.T("Properties.UnitUnassigned", "미배정", "Unassigned"))
        };
        options.AddRange(project.Stations.SelectMany(station => station.Units.Select(unit =>
            new LayoutPropertyOption(unit.Id, $"{unit.Name} / {unit.Id}"))));
        return options;
    }

    private static LayoutPropertyOption ToOption(LayoutComponentDefinition component) =>
        new(component.Id, DisplayName(component.Name, component.Id));

    private static string DisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}";
}
