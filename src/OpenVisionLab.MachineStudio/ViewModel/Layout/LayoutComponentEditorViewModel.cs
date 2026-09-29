using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record LayoutPropertyOption(string Id, string DisplayName);

internal readonly record struct LayoutPlacementDraft(
    string Name,
    double PositionX,
    double PositionDepth,
    double BaseElevation,
    double RotationDegrees,
    double FootprintWidth,
    double FootprintDepth,
    double VerticalHeight,
    bool ApplyVerticalHeight);

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

    internal LayoutComponentEditorViewModel(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutItem item,
        Action definitionChanged,
        Func<LayoutItem, LayoutPlacementDraft, bool> applyPlacementDraft,
        Func<LayoutItem, VirtualAxisDefinition, double, double, double, double, bool> applyAxisDriveDraft)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(layout);
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _component = item.Component ?? throw new ArgumentException(
            "A layout component is required.",
            nameof(item));
        _definitionChanged = definitionChanged ?? throw new ArgumentNullException(nameof(definitionChanged));
        _applyPlacementDraft = applyPlacementDraft ?? throw new ArgumentNullException(nameof(applyPlacementDraft));
        _applyAxisDriveDraft = applyAxisDriveDraft ?? throw new ArgumentNullException(nameof(applyAxisDriveDraft));
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
        _applyPlacementDraftCommand = new RelayCommand(_ => TryApplyPlacementDraft(), _ => HasPendingPlacementDraft, useCommandManagerRequery: false);
        _discardPlacementDraftCommand = new RelayCommand(_ => DiscardPlacementDraft(), _ => HasPendingPlacementDraft, useCommandManagerRequery: false);
        _applyAxisDriveDraftCommand = new RelayCommand(_ => TryApplyAxisDriveDraft(), _ => HasPendingAxisDriveDraft, useCommandManagerRequery: false);
        _discardAxisDriveDraftCommand = new RelayCommand(_ => DiscardAxisDriveDraft(), _ => HasPendingAxisDriveDraft, useCommandManagerRequery: false);
        DiscardAxisDriveDraft();
        _item.PropertyChanged += OnItemPropertyChanged;
        Validate();
    }

    public string Id => _component.Id;
    public string KindText => OpenVisionLanguageService.T(
        $"Properties.Value.{_component.Kind}",
        _component.Kind.ToString(),
        _component.Kind.ToString());
    public string PaneTitle => string.IsNullOrWhiteSpace(Name) ? KindText : Name;

    public string Name
    {
        get => _item.CurrentName;
        set
        {
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
        set => _item.SetCurrentX(value, snapToGrid: false);
    }

    public double Y
    {
        get => _item.CurrentY;
        set => _item.SetCurrentY(value, snapToGrid: false);
    }

    public double RotationDegrees
    {
        get => _item.CurrentRotationDegrees;
        set => _item.CurrentRotationDegrees = value;
    }

    public double Width
    {
        get => _item.CurrentWidth;
        set => _item.CurrentWidth = value;
    }

    public double Height
    {
        get => _item.CurrentHeight;
        set => _item.CurrentHeight = value;
    }

    public double VerticalBaseElevation
    {
        get => _item.CurrentVerticalBaseElevation;
        set => _item.CurrentVerticalBaseElevation = value;
    }

    public double VerticalHeight
    {
        get => _item.CurrentVerticalHeight;
        set => _item.CurrentVerticalHeight = value;
    }

    public bool IsVerticalEnvelopeEstimated => !_item.HasExplicitVerticalEnvelope;

    public string? BehaviorBindingId
    {
        get => _item.CurrentBehaviorBindingId;
        set
        {
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
        set => _item.CurrentUnitId = value;
    }

    public string DraftName
    {
        get => _draftName;
        set { if (SetProperty(ref _draftName, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftXText
    {
        get => _draftXText;
        set { if (SetProperty(ref _draftXText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftDepthText
    {
        get => _draftDepthText;
        set { if (SetProperty(ref _draftDepthText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftBaseElevationText
    {
        get => _draftBaseElevationText;
        set { if (SetProperty(ref _draftBaseElevationText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftRotationText
    {
        get => _draftRotationText;
        set { if (SetProperty(ref _draftRotationText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftFootprintWidthText
    {
        get => _draftFootprintWidthText;
        set { if (SetProperty(ref _draftFootprintWidthText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftFootprintDepthText
    {
        get => _draftFootprintDepthText;
        set { if (SetProperty(ref _draftFootprintDepthText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public string DraftVerticalHeightText
    {
        get => _draftVerticalHeightText;
        set { if (SetProperty(ref _draftVerticalHeightText, value ?? string.Empty)) OnPlacementDraftChanged(); }
    }

    public bool HasPendingPlacementDraft =>
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
        if (name.Length is < 1 or > 80 ||
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
                "이름·좌표를 확인하고 회전 -360~360°, 너비·깊이·높이 1~10000 LU 범위의 값을 입력하세요.",
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
            _item.HasExplicitVerticalEnvelope != _baselineHasExplicitVerticalEnvelope)
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
                !string.Equals(DraftVerticalHeightText, FormatDraftVerticalHeight(_baselineVerticalHeight), StringComparison.Ordinal));
        return true;
    }

    public void DiscardPlacementDraft()
    {
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
    public bool HasPendingInspectorDraft => HasPendingPlacementDraft || HasPendingAxisDriveDraft;
    public ICommand ApplyAxisDriveDraftCommand => _applyAxisDriveDraftCommand;
    public ICommand DiscardAxisDriveDraftCommand => _discardAxisDriveDraftCommand;

    public bool TryApplyInspectorDraft()
    {
        if (HasPendingPlacementDraft && !TryValidatePlacementDraft(out _)) return false;
        if (HasPendingAxisDriveDraft && !TryValidateAxisDriveDraft(out _, out _, out _, out _)) return false;
        return TryApplyPlacementDraft() && TryApplyAxisDriveDraft();
    }

    public void DiscardInspectorDraft()
    {
        DiscardPlacementDraft();
        DiscardAxisDriveDraft();
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

    public IReadOnlyList<LayoutPropertyOption> BehaviorBindingOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> DigitalInputOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> DigitalOutputOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> TargetComponentOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> ConveyorComponentOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> UnitOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> InspectionStateOptions =>
        Enum.GetValues<WorkpieceInspectionState>()
            .Select(state => new LayoutPropertyOption(
                state.ToString(),
                OpenVisionLanguageService.T(
                    $"Properties.Value.{state}",
                    state.ToString(),
                    state.ToString())))
            .ToArray();

    public bool HasBehaviorBinding => _component.Kind != LayoutComponentKind.MachineFrame;
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
        OnPropertyChanged(nameof(InspectionStateOptions));
        Validate();
    }

    public void Dispose() => _item.PropertyChanged -= OnItemPropertyChanged;

    private DeviceDefinition? LinkedDevice => _project.Devices.FirstOrDefault(device =>
        string.Equals(device.Id, _component.BehaviorBindingId, StringComparison.Ordinal));
    private DigitalSensorDefinition? Sensor => LinkedDevice?.Sensor;
    private PneumaticCylinderDefinition? Cylinder => LinkedDevice?.Cylinder;
    private ConveyorDefinition? Conveyor => LinkedDevice?.Conveyor;
    private WorkpieceDefinition? Workpiece => LinkedDevice?.Workpiece;

    private void UpdateSensor(Action<DigitalSensorDefinition> update, string propertyName)
    {
        if (Sensor is { } sensor)
        {
            update(sensor);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private void UpdateCylinder(Action<PneumaticCylinderDefinition> update, string propertyName)
    {
        if (Cylinder is { } cylinder)
        {
            update(cylinder);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private void UpdateConveyor(Action<ConveyorDefinition> update, string propertyName)
    {
        if (Conveyor is { } conveyor)
        {
            update(conveyor);
            NotifyBehaviorChanged(propertyName);
        }
    }

    private void UpdateWorkpiece(Action<WorkpieceDefinition> update, string propertyName)
    {
        if (Workpiece is { } workpiece)
        {
            update(workpiece);
            NotifyBehaviorChanged(propertyName);
        }
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
            Validate();
            if (!HasPendingPlacementDraft)
            {
                DiscardPlacementDraft();
            }
        }
    }

    private void OnPlacementDraftChanged()
    {
        PlacementDraftError = string.Empty;
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

    private static string LocalizeValidationMessage(MachineProjectLayoutValidationError error) =>
        error.Code switch
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
