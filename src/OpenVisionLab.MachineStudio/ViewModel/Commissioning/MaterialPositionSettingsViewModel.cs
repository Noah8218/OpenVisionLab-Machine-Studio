using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>One position's setup draft; the containing material settings owner commits it.</summary>
public sealed class MaterialPositionSettingsViewModel : ViewModelBase
{
    private readonly MachineLayoutDefinition _layout;
    private string? _unitId;
    private string? _conveyorId;
    private string? _route;

    public MaterialPositionSettingsViewModel(MachineLayoutDefinition layout, LayoutComponentDefinition component, IReadOnlyList<MachineUnitDefinition> units)
    {
        _layout = layout;
        Id = component.Id;
        Name = component.Name;
        IsExit = component.Kind == LayoutComponentKind.ExitPosition;
        Units = units;
        _unitId = component.UnitId;
        _conveyorId = component.MaterialConveyorId;
        _route = component.MaterialRoute;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsExit { get; }
    public IReadOnlyList<MachineUnitDefinition> Units { get; }
    public string? UnitId
    {
        get => _unitId;
        set
        {
            if (!SetProperty(ref _unitId, value)) return;
            if (!Conveyors.Any(conveyor => conveyor.Id == ConveyorId)) ConveyorId = null;
            OnPropertyChanged(nameof(Conveyors));
        }
    }
    public string? ConveyorId { get => _conveyorId; set => SetProperty(ref _conveyorId, value); }
    public string? Route { get => _route; set => SetProperty(ref _route, value); }
    public IReadOnlyList<LayoutComponentDefinition> Conveyors => _layout.Components
        .Where(component => component.Kind == LayoutComponentKind.Conveyor && component.UnitId == UnitId).ToArray();
    public IReadOnlyList<LayoutPropertyOption> Routes => [new("auto", OpenVisionLanguageService.T("MaterialFlow.AutoRoute")), new("OK", "OK"), new("NG", "NG")];
}
