using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Models;

namespace OpenVisionLab.MachineStudio.Model;

public enum LayoutItemKind
{
    Axis,
    Device,
    MachineFrame,
    LinearStage,
    RotaryStage,
    DigitalSensor,
    PneumaticCylinder,
    Conveyor,
    Workpiece
}

public sealed class LayoutItem : INotifyPropertyChanged
{
    private readonly string _legacyName;
    private readonly Coordinate3D _legacyPosition;
    private readonly LayoutComponentDefinition? _component;
    private readonly double _gridSize;
    private readonly bool _snapToGrid;
    private double _currentX;
    private double _currentY;
    private bool _isSelected;

    public string Id { get; }
    public string Name => _component?.Name ?? _legacyName;
    public LayoutItemKind Kind { get; }
    public Coordinate3D Position => _component is null
        ? _legacyPosition
        : new Coordinate3D(_currentX, _currentY, 0);
    public object? Model { get; }
    public LayoutComponentDefinition? Component => _component;
    public double Width => _component?.Size.Width ?? 80;
    public double Height => _component?.Size.Height ?? 40;
    public double RotationDegrees => _component?.Transform.RotationDegrees ?? 0;
    public int ZIndex => _component?.ZIndex ?? 0;
    public string? BehaviorBindingId => _component?.BehaviorBindingId;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string CurrentName
    {
        get => Name;
        set
        {
            if (_component is null || string.Equals(_component.Name, value, StringComparison.Ordinal))
            {
                return;
            }

            _component.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Name));
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double CurrentX
    {
        get => _currentX;
        set => SetCurrentX(value, snapToGrid: true);
    }

    internal void SetCurrentX(double value, bool snapToGrid)
    {
        var normalized = NormalizeCoordinate(value, snapToGrid);
        if (_currentX == normalized)
        {
            return;
        }

        _currentX = normalized;
        if (_component is not null)
        {
            _component.Transform.X = normalized;
        }
        OnPropertyChanged(nameof(CurrentX));
        OnPropertyChanged(nameof(Position));
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    public double CurrentY
    {
        get => _currentY;
        set => SetCurrentY(value, snapToGrid: true);
    }

    internal void SetCurrentY(double value, bool snapToGrid)
    {
        var normalized = NormalizeCoordinate(value, snapToGrid);
        if (_currentY == normalized)
        {
            return;
        }

        _currentY = normalized;
        if (_component is not null)
        {
            _component.Transform.Y = normalized;
        }
        OnPropertyChanged(nameof(CurrentY));
        OnPropertyChanged(nameof(Position));
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    public double CurrentRotationDegrees
    {
        get => RotationDegrees;
        set
        {
            if (_component is null || _component.Transform.RotationDegrees == value)
            {
                return;
            }

            _component.Transform.RotationDegrees = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RotationDegrees));
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double CurrentWidth
    {
        get => Width;
        set
        {
            if (_component is null || _component.Size.Width == value)
            {
                return;
            }

            _component.Size.Width = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Width));
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double CurrentHeight
    {
        get => Height;
        set
        {
            if (_component is null || _component.Size.Height == value)
            {
                return;
            }

            _component.Size.Height = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Height));
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? CurrentBehaviorBindingId
    {
        get => BehaviorBindingId;
        set
        {
            if (_component is null || string.Equals(
                    _component.BehaviorBindingId,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            _component.BehaviorBindingId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BehaviorBindingId));
            DefinitionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void SetZIndex(int value)
    {
        if (_component is null || _component.ZIndex == value)
        {
            return;
        }

        _component.ZIndex = value;
        OnPropertyChanged(nameof(ZIndex));
        DefinitionChanged?.Invoke(this, EventArgs.Empty);
    }

    public LayoutItem(string id, string name, LayoutItemKind kind, Coordinate3D position, object? model = null)
    {
        Id = id;
        _legacyName = name;
        Kind = kind;
        _legacyPosition = position;
        Model = model;
        _gridSize = 1;
        _currentX = position.X;
        _currentY = position.Y;
    }

    public LayoutItem(LayoutComponentDefinition component, double gridSize, bool snapToGrid)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!double.IsFinite(gridSize) || gridSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gridSize));
        }

        _component = component;
        _gridSize = gridSize;
        _snapToGrid = snapToGrid;
        Id = component.Id;
        _legacyName = component.Name;
        Kind = component.Kind switch
        {
            LayoutComponentKind.MachineFrame => LayoutItemKind.MachineFrame,
            LayoutComponentKind.LinearStage => LayoutItemKind.LinearStage,
            LayoutComponentKind.RotaryStage => LayoutItemKind.RotaryStage,
            LayoutComponentKind.DigitalSensor => LayoutItemKind.DigitalSensor,
            LayoutComponentKind.PneumaticCylinder => LayoutItemKind.PneumaticCylinder,
            LayoutComponentKind.Conveyor => LayoutItemKind.Conveyor,
            LayoutComponentKind.Workpiece => LayoutItemKind.Workpiece,
            _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, null)
        };
        Model = component;
        _legacyPosition = new Coordinate3D(component.Transform.X, component.Transform.Y, 0);
        _currentX = component.Transform.X;
        _currentY = component.Transform.Y;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? DefinitionChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private double NormalizeCoordinate(double value, bool snapToGrid)
    {
        if (!snapToGrid || !_snapToGrid || !double.IsFinite(value))
        {
            return value;
        }

        return Math.Round(value / _gridSize, MidpointRounding.AwayFromZero) * _gridSize;
    }
}
