using System;
using System.Collections.Generic;
using System.Linq;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Analysis;

public sealed record SignalTimelineSample(
    long TickIndex,
    TimeSpan SimulationTime,
    bool Value);

public sealed record SignalTimeline(
    string SignalId,
    IReadOnlyList<SignalTimelineSample> Samples);

public static class SimulationSignalTimelineAnalyzer
{
    public static IReadOnlyList<SignalTimeline> AnalyzeSignals(IReadOnlyList<SimulationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        if (snapshots.Count == 0)
        {
            return Array.Empty<SignalTimeline>();
        }

        var signalTimelineSamples = new Dictionary<string, List<SignalTimelineSample>>(StringComparer.Ordinal);
        var lastValues = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var snapshot in snapshots.OrderBy(snapshot => snapshot.TickIndex))
        {
            foreach (var signal in snapshot.Signals.OrderBy(signal => signal.Id, StringComparer.Ordinal))
            {
                if (!signalTimelineSamples.TryGetValue(signal.Id, out List<SignalTimelineSample>? samples))
                {
                    samples = new List<SignalTimelineSample>();
                    signalTimelineSamples[signal.Id] = samples;
                    samples.Add(CreateSample(signal.Id, snapshot));
                    lastValues[signal.Id] = signal.Value;
                    continue;
                }

                if (lastValues[signal.Id] != signal.Value)
                {
                    samples.Add(CreateSample(signal.Id, snapshot));
                    lastValues[signal.Id] = signal.Value;
                }
            }
        }

        return signalTimelineSamples
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new SignalTimeline(item.Key, item.Value))
            .ToArray();
    }

    public static IReadOnlyList<SignalTimelineSample> GetSignalTimeline(
        IReadOnlyList<SimulationSnapshot> snapshots,
        string signalId)
    {
        if (string.IsNullOrWhiteSpace(signalId))
        {
            throw new ArgumentException("Signal id is required.", nameof(signalId));
        }

        var timeline = AnalyzeSignals(snapshots)
            .SingleOrDefault(item => string.Equals(item.SignalId, signalId, StringComparison.Ordinal));
        if (timeline is null)
        {
            throw new KeyNotFoundException($"Signal '{signalId}' was not observed in snapshots.");
        }

        return timeline.Samples;
    }

    private static SignalTimelineSample CreateSample(string signalId, SimulationSnapshot snapshot)
    {
        var value = snapshot.Signals.Single(signal => signal.Id == signalId).Value;
        return new SignalTimelineSample(snapshot.TickIndex, snapshot.SimulationTime, value);
    }
}
