using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record LayoutAuthoringState(
    string DefinitionJson,
    IReadOnlyList<string> SelectedComponentIds,
    string? PrimaryComponentId);

internal sealed class LayoutEditHistory
{
    private const int Capacity = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly List<LayoutEdit> _undo = new();
    private readonly List<LayoutEdit> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public LayoutAuthoringState Capture(
        MachineProjectDocument project,
        IEnumerable<string> selectedComponentIds,
        string? primaryComponentId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selectedComponentIds);
        var document = new LayoutAuthoringDocument
        {
            ActiveLayoutId = project.Simulation.ActiveLayoutId,
            Layouts = project.Layouts,
            Axes = project.Axes,
            Devices = project.Devices,
            Channels = project.Channels
        };
        return new LayoutAuthoringState(
            JsonSerializer.Serialize(document, JsonOptions),
            selectedComponentIds.ToArray(),
            primaryComponentId);
    }

    public void Restore(MachineProjectDocument project, LayoutAuthoringState state)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(state);
        var document = JsonSerializer.Deserialize<LayoutAuthoringDocument>(
                state.DefinitionJson,
                JsonOptions)
            ?? throw new InvalidOperationException("Failed to restore layout authoring state.");
        project.Simulation.ActiveLayoutId = document.ActiveLayoutId;
        project.Layouts = document.Layouts ?? new List<MachineLayoutDefinition>();
        project.Axes = document.Axes ?? new List<VirtualAxisDefinition>();
        project.Devices = document.Devices ?? new List<DeviceDefinition>();
        project.Channels = document.Channels ?? new List<ChannelDefinition>();
    }

    public bool Record(LayoutAuthoringState before, LayoutAuthoringState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (string.Equals(before.DefinitionJson, after.DefinitionJson, StringComparison.Ordinal))
        {
            return false;
        }

        _undo.Add(new LayoutEdit(before, after));
        if (_undo.Count > Capacity)
        {
            // ponytail: bounded full snapshots stay simple; use deltas only if real project sizes require them.
            _undo.RemoveAt(0);
        }
        _redo.Clear();
        return true;
    }

    public bool TryUndo(out LayoutAuthoringState? state)
    {
        if (_undo.Count == 0)
        {
            state = null;
            return false;
        }

        var edit = Pop(_undo);
        _redo.Add(edit);
        state = edit.Before;
        return true;
    }

    public bool TryRedo(out LayoutAuthoringState? state)
    {
        if (_redo.Count == 0)
        {
            state = null;
            return false;
        }

        var edit = Pop(_redo);
        _undo.Add(edit);
        state = edit.After;
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static LayoutEdit Pop(List<LayoutEdit> edits)
    {
        var index = edits.Count - 1;
        var edit = edits[index];
        edits.RemoveAt(index);
        return edit;
    }

    private sealed record LayoutEdit(LayoutAuthoringState Before, LayoutAuthoringState After);

    private sealed class LayoutAuthoringDocument
    {
        public string? ActiveLayoutId { get; init; }
        public List<MachineLayoutDefinition>? Layouts { get; init; }
        public List<VirtualAxisDefinition>? Axes { get; init; }
        public List<DeviceDefinition>? Devices { get; init; }
        public List<ChannelDefinition>? Channels { get; init; }
    }
}
