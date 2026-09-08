using System.Collections.ObjectModel;
using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.MachineStudio.Models.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed record RuntimeTimelineItem(
    long EventIndex,
    long TickIndex,
    string TimeText,
    string Category,
    string Code,
    string Message)
{
    public string HeaderText => $"{TimeText} · tick {TickIndex}";
}

/// <summary>
/// Owns the bounded runtime-event history and its localized timeline rows.
/// </summary>
internal sealed class RuntimeTimelineViewModel : ViewModelBase
{
    private const int RetentionLimit = 200;
    private readonly List<SimulationEvent> _events = [];

    internal ObservableCollection<RuntimeTimelineItem> Items { get; } = new();

    internal bool HasItems => Items.Count > 0;

    internal string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(
            "Debugger.TimelineCount",
            "최근 이벤트 {0}/200건",
            "Latest {0}/200 events"),
        Items.Count);

    internal void Add(SimulationEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        _events.Insert(0, runtimeEvent);
        if (_events.Count > RetentionLimit)
        {
            _events.RemoveRange(RetentionLimit, _events.Count - RetentionLimit);
        }

        Rebuild();
    }

    internal void Clear()
    {
        _events.Clear();
        Items.Clear();
        NotifyPresentationChanged();
    }

    internal void RefreshLocalization() => Rebuild();

    private void Rebuild()
    {
        Items.Clear();
        foreach (SimulationEvent item in _events)
        {
            Items.Add(new RuntimeTimelineItem(
                item.EventIndex,
                item.TickIndex,
                item.SimulationTime.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
                OpenVisionLanguageService.T($"Runtime.Category.{item.Category}", item.Category, item.Category),
                item.Code,
                SimulationLogEntry.LocalizeMessage(item.Message)));
        }
        NotifyPresentationChanged();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SummaryText));
    }
}
