using System.Collections.ObjectModel;
using System.Reflection;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class PropertiesViewModel : ViewModelBase
{
    private readonly ObservableCollection<PropertyItem> _items = new();
    private object? _currentModel;

    public PropertiesViewModel()
    {
        Show(null);
    }

    public ObservableCollection<PropertyItem> Items => _items;

    public void RefreshLocalization() => Show(_currentModel);

    public void Show(object? model)
    {
        _currentModel = model;
        _items.Clear();
        if (model is null)
        {
            _items.Add(new PropertyItem(
                LocalizeLabel("Message"),
                OpenVisionLanguageService.T(
                    "Properties.EmptyMessage",
                    "항목을 선택하면 속성을 확인할 수 있습니다.",
                    "Select an item to view properties.")));
            return;
        }

        if (model is LayoutComponentDefinition layoutComponent)
        {
            ShowLayoutComponent(layoutComponent);
            return;
        }

        var type = model.GetType();
        _items.Add(new PropertyItem(
            LocalizeLabel("Type"),
            ModelTypeDisplayName(model),
            LocalizeCategory("Metadata")));

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                                    .OrderBy(p => p.Name))
        {
            var value = property.GetValue(model);
            if (property.Name == nameof(DeviceDefinition.Camera))
            {
                if (value is VirtualCameraDefinition camera)
                {
                    _items.Add(new PropertyItem(
                        LocalizeLabel("ExposureDelay"),
                        $"{camera.ExposureDelayMilliseconds} ms",
                        LocalizeCategory("Camera")));
                    _items.Add(new PropertyItem(
                        LocalizeLabel("TransferDelay"),
                        $"{camera.TransferDelayMilliseconds} ms",
                        LocalizeCategory("Camera")));
                    _items.Add(new PropertyItem(
                        LocalizeLabel("PlaceholderResult"),
                        LocalizeValue(camera.PlaceholderDecision.ToString()),
                        LocalizeCategory("Camera")));
                }

                continue;
            }

            if (property.Name == nameof(DeviceDefinition.Sensor))
            {
                if (value is DigitalSensorDefinition sensor)
                {
                    _items.Add(new PropertyItem(LocalizeLabel("OutputChannel"), sensor.OutputChannelId, LocalizeCategory("Sensor")));
                    _items.Add(new PropertyItem(LocalizeLabel("TargetComponent"), sensor.TargetComponentId, LocalizeCategory("Sensor")));
                    _items.Add(new PropertyItem(LocalizeLabel("OnDelay"), $"{sensor.OnDelayMilliseconds} ms", LocalizeCategory("Sensor")));
                    _items.Add(new PropertyItem(LocalizeLabel("OffDelay"), $"{sensor.OffDelayMilliseconds} ms", LocalizeCategory("Sensor")));
                }

                continue;
            }

            if (property.Name == nameof(DeviceDefinition.Cylinder))
            {
                if (value is PneumaticCylinderDefinition cylinder)
                {
                    _items.Add(new PropertyItem(LocalizeLabel("ExtendCommand"), cylinder.ExtendCommandChannelId, LocalizeCategory("Cylinder")));
                    _items.Add(new PropertyItem(LocalizeLabel("ExtendedSensor"), cylinder.ExtendedSensorChannelId, LocalizeCategory("Cylinder")));
                    _items.Add(new PropertyItem(LocalizeLabel("RetractedSensor"), cylinder.RetractedSensorChannelId, LocalizeCategory("Cylinder")));
                    _items.Add(new PropertyItem(LocalizeLabel("ExtendDuration"), $"{cylinder.ExtendDurationMilliseconds} ms", LocalizeCategory("Cylinder")));
                    _items.Add(new PropertyItem(LocalizeLabel("RetractDuration"), $"{cylinder.RetractDurationMilliseconds} ms", LocalizeCategory("Cylinder")));
                    _items.Add(new PropertyItem(LocalizeLabel("Stroke"), cylinder.Stroke.ToString("G6"), LocalizeCategory("Cylinder")));
                }

                continue;
            }

            if (property.Name == nameof(DeviceDefinition.Conveyor))
            {
                if (value is ConveyorDefinition conveyor)
                {
                    _items.Add(new PropertyItem(LocalizeLabel("RunCommand"), conveyor.RunCommandChannelId, LocalizeCategory("Conveyor")));
                    _items.Add(new PropertyItem(LocalizeLabel("ReverseCommand"), conveyor.ReverseCommandChannelId, LocalizeCategory("Conveyor")));
                    _items.Add(new PropertyItem(LocalizeLabel("Speed"), $"{conveyor.SpeedUnitsPerSecond:G6} units/s", LocalizeCategory("Conveyor")));
                }

                continue;
            }

            if (property.Name == nameof(DeviceDefinition.Workpiece))
            {
                if (value is WorkpieceDefinition workpiece)
                {
                    _items.Add(new PropertyItem(LocalizeLabel("Type"), workpiece.Type, LocalizeCategory("Workpiece")));
                    _items.Add(new PropertyItem(LocalizeLabel("Conveyor"), workpiece.ConveyorComponentId, LocalizeCategory("Workpiece")));
                    _items.Add(new PropertyItem(LocalizeLabel("InspectionState"), LocalizeValue(workpiece.InspectionState.ToString()), LocalizeCategory("Workpiece")));
                }

                continue;
            }

            var valueText = FormatPropertyValue(value);

            _items.Add(new PropertyItem(
                LocalizeLabel(property.Name),
                valueText,
                LocalizeCategory("Properties"),
                isEditable: property.CanWrite));
        }
    }

    private void ShowLayoutComponent(LayoutComponentDefinition component)
    {
        _items.Clear();
        _items.Add(new PropertyItem(LocalizeLabel("Type"), LocalizeValue(component.Kind.ToString()), LocalizeCategory("Metadata")));
        _items.Add(new PropertyItem(LocalizeLabel("Id"), component.Id, LocalizeCategory("Metadata")));
        _items.Add(new PropertyItem(LocalizeLabel("Name"), component.Name, LocalizeCategory("General")));
        _items.Add(new PropertyItem(LocalizeLabel("X"), component.Transform.X.ToString("G6"), LocalizeCategory("Transform"), true));
        _items.Add(new PropertyItem(LocalizeLabel("Y"), component.Transform.Y.ToString("G6"), LocalizeCategory("Transform"), true));
        _items.Add(new PropertyItem(
            LocalizeLabel("Rotation"),
            $"{component.Transform.RotationDegrees:G6}°",
            LocalizeCategory("Transform"),
            true));
        _items.Add(new PropertyItem(LocalizeLabel("Width"), component.Size.Width.ToString("G6"), LocalizeCategory("Bounds"), true));
        _items.Add(new PropertyItem(LocalizeLabel("Height"), component.Size.Height.ToString("G6"), LocalizeCategory("Bounds"), true));
        _items.Add(new PropertyItem(
            LocalizeLabel("Behavior"),
            component.BehaviorBindingId ?? LocalizeValue("Static"),
            LocalizeCategory("Simulation")));
    }

    public void ShowNode(TreeNodeViewModel? node)
    {
        if (node is null)
        {
            Show(null);
            return;
        }

        Show(node.Model);
    }

    private static string LocalizeLabel(string value) =>
        OpenVisionLanguageService.T($"Properties.{value}", value, value);

    private static string LocalizeCategory(string value) => LocalizeLabel(value);

    private static string ModelTypeDisplayName(object model) => model switch
    {
        MachineProjectDocument => OpenVisionLanguageService.T("Properties.Model.Project"),
        MachineLayoutDefinition => OpenVisionLanguageService.T("Properties.Model.Layout"),
        VirtualAxisDefinition => OpenVisionLanguageService.T("Properties.Model.Axis"),
        DeviceDefinition => OpenVisionLanguageService.T("Properties.Model.Device"),
        ChannelDefinition => OpenVisionLanguageService.T("Properties.Model.Channel"),
        SequenceDefinition => OpenVisionLanguageService.T("Properties.Model.Sequence"),
        SequenceStepDefinition => OpenVisionLanguageService.T("Properties.Model.SequenceStep"),
        _ => OpenVisionLanguageService.T("Properties.Model.Item")
    };

    private static string FormatPropertyValue(object? value) => value switch
    {
        null => OpenVisionLanguageService.T("Properties.ValueNull"),
        string text => text,
        bool boolean => OpenVisionLanguageService.T(
            boolean ? "Properties.ValueTrue" : "Properties.ValueFalse"),
        Coordinate3D coordinate => string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            "X {0:G6}, Y {1:G6}, Z {2:G6}",
            coordinate.X,
            coordinate.Y,
            coordinate.Z),
        DateTimeOffset timestamp => timestamp.ToString(
            "yyyy-MM-dd HH:mm:ss zzz",
            System.Globalization.CultureInfo.CurrentCulture),
        DateTime timestamp => timestamp.ToString(
            "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.CurrentCulture),
        TimeSpan duration => duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
        Enum enumeration => LocalizeValue(enumeration.ToString()),
        IReadOnlyDictionary<string, string> values => values.Count == 0
            ? OpenVisionLanguageService.T("Properties.ValueNone")
            : string.Join(", ", values.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}")),
        System.Collections.IEnumerable values => FormatCollection(values),
        IFormattable formattable => formattable.ToString(
            "G6",
            System.Globalization.CultureInfo.CurrentCulture),
        _ => OpenVisionLanguageService.T("Properties.ValueConfigured")
    };

    private static string FormatCollection(System.Collections.IEnumerable values)
    {
        var items = values.Cast<object?>().ToArray();
        if (items.Length == 0)
        {
            return OpenVisionLanguageService.T("Properties.ValueNone");
        }

        const int displayLimit = 5;
        var labels = items.Take(displayLimit).Select(FormatCollectionItem).ToList();
        if (items.Length > displayLimit)
        {
            labels.Add($"+{items.Length - displayLimit}");
        }

        return string.Join(", ", labels);
    }

    private static string FormatCollectionItem(object? item)
    {
        if (item is null)
        {
            return OpenVisionLanguageService.T("Properties.ValueNull");
        }

        if (item is string or Enum or IFormattable)
        {
            return FormatPropertyValue(item);
        }

        var type = item.GetType();
        var name = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(item) as string;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var id = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(item) as string;
        return string.IsNullOrWhiteSpace(id)
            ? OpenVisionLanguageService.T("Properties.ValueConfigured")
            : id;
    }

    private static string LocalizeValue(string value) =>
        OpenVisionLanguageService.T(
            value switch
            {
                "True" => "Properties.ValueTrue",
                "False" => "Properties.ValueFalse",
                "(null)" => "Properties.ValueNull",
                "None" => "Properties.ValueNone",
                "Normal" => "Properties.ValueNormal",
                "Fault" => "Properties.ValueFault",
                "Ready" => "Properties.ValueReady",
                "Running" => "Properties.ValueRunning",
                "Stopped" => "Properties.ValueStopped",
                "Idle" => "Properties.ValueIdle",
                "Moving" => "Properties.ValueMoving",
                "Extended" => "Properties.ValueExtended",
                "Retracted" => "Properties.ValueRetracted",
                "Extending" => "Properties.ValueExtending",
                "Retracting" => "Properties.ValueRetracting",
                "Pass" => "Properties.ValuePass",
                "Fail" => "Properties.ValueFail",
                "Pending" => "Properties.ValuePending",
                _ => $"Properties.Value.{value}"
            },
            value,
            value);
}
