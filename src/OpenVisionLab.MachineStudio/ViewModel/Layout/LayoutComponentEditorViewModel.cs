using System.ComponentModel;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record LayoutPropertyOption(string Id, string DisplayName);

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
    private bool _hasValidationErrors;
    private string _validationMessage = string.Empty;

    public LayoutComponentEditorViewModel(
        MachineProjectDocument project,
        MachineLayoutDefinition layout,
        LayoutItem item,
        Action definitionChanged)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(layout);
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _component = item.Component ?? throw new ArgumentException(
            "A layout component is required.",
            nameof(item));
        _definitionChanged = definitionChanged ?? throw new ArgumentNullException(nameof(definitionChanged));

        BehaviorBindingOptions = BuildBehaviorBindingOptions(project, _component.Kind);
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

        _item.PropertyChanged += OnItemPropertyChanged;
        Validate();
    }

    public string Id => _component.Id;
    public string KindText => OpenVisionLanguageService.T(
        $"Properties.Value.{_component.Kind}",
        _component.Kind.ToString(),
        _component.Kind.ToString());

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
        set => _item.CurrentX = value;
    }

    public double Y
    {
        get => _item.CurrentY;
        set => _item.CurrentY = value;
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

    public string? BehaviorBindingId
    {
        get => _item.CurrentBehaviorBindingId;
        set
        {
            if (string.Equals(_item.CurrentBehaviorBindingId, value, StringComparison.Ordinal))
            {
                return;
            }

            _item.CurrentBehaviorBindingId = value;
            OnPropertyChanged(string.Empty);
            Validate();
        }
    }

    public IReadOnlyList<LayoutPropertyOption> BehaviorBindingOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> DigitalInputOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> DigitalOutputOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> TargetComponentOptions { get; }
    public IReadOnlyList<LayoutPropertyOption> ConveyorComponentOptions { get; }
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
            nameof(LayoutItem.CurrentBehaviorBindingId) or nameof(LayoutItem.BehaviorBindingId) => nameof(BehaviorBindingId),
            _ => null
        };
        if (editorProperty is not null)
        {
            OnPropertyChanged(editorProperty);
            Validate();
        }
    }

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
                "Check the authored properties.") + $" {errors[0].Message}" +
              (errors.Length > 1 ? $" (+{errors.Length - 1})" : string.Empty);
    }

    private static int ConvertToInt(double value) => checked((int)Math.Round(value));

    private static IReadOnlyList<LayoutPropertyOption> BuildBehaviorBindingOptions(
        MachineProjectDocument project,
        LayoutComponentKind kind) => kind switch
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
        _ => Array.Empty<LayoutPropertyOption>()
    };

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

    private static LayoutPropertyOption ToOption(LayoutComponentDefinition component) =>
        new(component.Id, DisplayName(component.Name, component.Id));

    private static string DisplayName(string? name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : $"{name} — {id}";
}
