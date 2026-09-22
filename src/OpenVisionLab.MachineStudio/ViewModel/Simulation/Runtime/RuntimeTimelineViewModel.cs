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
    public string? CommandId { get; init; }

    public string CommandIdText => string.IsNullOrEmpty(CommandId)
        ? OpenVisionLanguageService.T(
            "Debugger.TimelineNoCommand",
            "명령 없음",
            "No command")
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Debugger.TimelineCommandId",
                "명령 ID · {0}",
                "Command ID · {0}"),
            CommandId);

    public string CommandIdTooltip => CommandId ?? OpenVisionLanguageService.T(
        "Debugger.TimelineNoCommand",
        "명령 없음",
        "No command");

    public string HeaderText => $"{TimeText} · tick {TickIndex}";
}

public sealed record RuntimeTimelineFilterItem(string Key, string DisplayText);

/// <summary>
/// Owns the bounded runtime-event history and its localized timeline rows.
/// </summary>
internal sealed class RuntimeTimelineViewModel : ViewModelBase
{
    private const int RetentionLimit = 200;
    private readonly List<SimulationEvent> _events = [];
    private string _selectedCategory = string.Empty;
    private SimulationLogSeverity? _selectedSeverity;
    private IReadOnlyList<RuntimeTimelineFilterItem> _filterOptions = [];
    private IReadOnlyList<RuntimeTimelineFilterItem> _severityFilterOptions = [];

    internal RuntimeTimelineViewModel()
    {
        RebuildFilterOptions();
        RebuildSeverityFilterOptions();
    }

    internal ObservableCollection<RuntimeTimelineItem> Items { get; } = new();

    internal IReadOnlyList<RuntimeTimelineFilterItem> FilterOptions => _filterOptions;

    internal IReadOnlyList<RuntimeTimelineFilterItem> SeverityFilterOptions => _severityFilterOptions;

    internal RuntimeTimelineFilterItem? SelectedFilter => _filterOptions.FirstOrDefault(
        item => string.Equals(item.Key, _selectedCategory, StringComparison.Ordinal));

    internal RuntimeTimelineFilterItem? SelectedSeverityFilter => _severityFilterOptions.FirstOrDefault(
        item => string.Equals(item.Key, GetSeverityKey(_selectedSeverity), StringComparison.Ordinal));

    internal bool HasItems => Items.Count > 0;

    internal bool HasEvents => _events.Count > 0;

    internal string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T(
            "Debugger.TimelineCount",
            "최근 이벤트 {0}/200건",
            "Latest {0}/200 events"),
        Items.Count);

    internal bool SetCategory(string? category)
    {
        var normalized = string.IsNullOrWhiteSpace(category) ? string.Empty : category;
        if (!string.IsNullOrEmpty(normalized)
            && !_filterOptions.Any(item => string.Equals(item.Key, normalized, StringComparison.Ordinal)))
        {
            return false;
        }

        if (string.Equals(_selectedCategory, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        _selectedCategory = normalized;
        Rebuild();
        return true;
    }

    internal bool SetSeverity(string? severity)
    {
        var normalized = string.IsNullOrWhiteSpace(severity) ? null : severity;
        if (normalized is not null
            && !_severityFilterOptions.Any(item =>
                string.Equals(item.Key, normalized, StringComparison.Ordinal)))
        {
            return false;
        }

        SimulationLogSeverity? selectedSeverity = normalized is null
            ? null
            : Enum.Parse<SimulationLogSeverity>(normalized, ignoreCase: false);
        if (_selectedSeverity == selectedSeverity)
        {
            return false;
        }

        _selectedSeverity = selectedSeverity;
        Rebuild();
        return true;
    }

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
        _selectedCategory = string.Empty;
        _selectedSeverity = null;
        Items.Clear();
        RebuildFilterOptions();
        RebuildSeverityFilterOptions();
        NotifyPresentationChanged();
    }

    internal void RefreshLocalization() => Rebuild();

    private void Rebuild()
    {
        RebuildFilterOptions();
        RebuildSeverityFilterOptions();
        Items.Clear();
        foreach (SimulationEvent item in _events.Where(item =>
            string.IsNullOrEmpty(_selectedCategory)
            || string.Equals(item.Category, _selectedCategory, StringComparison.Ordinal))
            .Where(item => _selectedSeverity is null
                || RuntimeObservabilityJournal.SeverityForCategory(item.Category) == _selectedSeverity))
        {
            Items.Add(new RuntimeTimelineItem(
                item.EventIndex,
                item.TickIndex,
                item.SimulationTime.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
                OpenVisionLanguageService.T($"Runtime.Category.{item.Category}", item.Category, item.Category),
                item.Code,
                SimulationLogEntry.LocalizeMessage(item.Message))
            {
                CommandId = item.CommandId
            });
        }
        NotifyPresentationChanged();
    }

    private void RebuildFilterOptions()
    {
        var categoryKeys = new List<string> { string.Empty };
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (SimulationEvent item in _events)
        {
            if (seen.Add(item.Category))
            {
                categoryKeys.Add(item.Category);
            }
        }

        if (!string.IsNullOrEmpty(_selectedCategory) && seen.Add(_selectedCategory))
        {
            categoryKeys.Add(_selectedCategory);
        }

        _filterOptions = categoryKeys
            .Select(key => new RuntimeTimelineFilterItem(
                key,
                string.IsNullOrEmpty(key)
                    ? OpenVisionLanguageService.T(
                        "Debugger.TimelineFilterAll",
                        "전체 이벤트",
                        "All events")
                    : OpenVisionLanguageService.T($"Runtime.Category.{key}", key, key)))
            .ToArray();
    }

    private void RebuildSeverityFilterOptions()
    {
        _severityFilterOptions = new[]
            {
                new RuntimeTimelineFilterItem(
                    string.Empty,
                    OpenVisionLanguageService.T(
                        "Debugger.TimelineSeverityFilterAll",
                        "전체 심각도",
                        "All severities"))
            }
            .Concat(Enum.GetValues<SimulationLogSeverity>().Select(severity =>
                new RuntimeTimelineFilterItem(
                    GetSeverityKey(severity),
                    OpenVisionLanguageService.T(
                        $"Runtime.{GetSeverityKey(severity)}",
                        GetSeverityKey(severity),
                        GetSeverityKey(severity)))))
            .ToArray();
    }

    private static string GetSeverityKey(SimulationLogSeverity? severity) => severity?.ToString() ?? string.Empty;

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SummaryText));
    }
}
