using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

public enum DeterministicScenarioAssertionKind
{
    AutomaticCycleCompleted,
    NoActiveFaults,
    FinalEquipmentState
}

public sealed record DeterministicScenarioAssertion(
    string AssertionId,
    DeterministicScenarioAssertionKind Kind,
    string? TargetId = null,
    string? ExpectedState = null,
    long MinimumCount = 1)
{
    public static ImmutableArray<DeterministicScenarioAssertion> FromProjectDefinitions(
        IEnumerable<TestScenarioAssertionDefinition>? definitions) =>
        definitions?.Select(definition => new DeterministicScenarioAssertion(
            definition.AssertionId,
            definition.Kind switch
            {
                TestScenarioAssertionKind.AutomaticCycleCompleted =>
                    DeterministicScenarioAssertionKind.AutomaticCycleCompleted,
                TestScenarioAssertionKind.NoActiveFaults =>
                    DeterministicScenarioAssertionKind.NoActiveFaults,
                TestScenarioAssertionKind.FinalEquipmentState =>
                    DeterministicScenarioAssertionKind.FinalEquipmentState,
                _ => throw new InvalidOperationException(
                    $"Unsupported project scenario assertion kind '{definition.Kind}'.")
            },
            definition.TargetId,
            definition.ExpectedState,
            definition.MinimumCount)).ToImmutableArray()
        ?? ImmutableArray<DeterministicScenarioAssertion>.Empty;

    internal static DeterministicScenarioAssertion Normalize(
        DeterministicScenarioAssertion assertion)
    {
        var normalized = assertion with
        {
            AssertionId = assertion.AssertionId?.Trim() ?? string.Empty,
            TargetId = string.IsNullOrWhiteSpace(assertion.TargetId)
                ? null
                : assertion.TargetId.Trim(),
            ExpectedState = string.IsNullOrWhiteSpace(assertion.ExpectedState)
                ? null
                : assertion.ExpectedState.Trim()
        };
        return normalized.Kind switch
        {
            DeterministicScenarioAssertionKind.AutomaticCycleCompleted => normalized with
            {
                TargetId = null,
                ExpectedState = null
            },
            DeterministicScenarioAssertionKind.NoActiveFaults => normalized with
            {
                TargetId = null,
                ExpectedState = null,
                MinimumCount = 1
            },
            DeterministicScenarioAssertionKind.FinalEquipmentState => normalized with
            {
                MinimumCount = 1
            },
            _ => normalized
        };
    }

    internal static IReadOnlyList<string> Validate(
        DeterministicScenarioAssertion assertion)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(assertion.AssertionId))
        {
            errors.Add("AssertionId is required.");
        }
        if (!Enum.IsDefined(assertion.Kind))
        {
            errors.Add($"Assertion kind '{assertion.Kind}' is not supported.");
        }
        if (assertion.Kind == DeterministicScenarioAssertionKind.AutomaticCycleCompleted
            && assertion.MinimumCount is < 1 or > int.MaxValue)
        {
            errors.Add($"Assertion '{assertion.AssertionId}' MinimumCount must be between 1 and {int.MaxValue}.");
        }
        if (assertion.Kind == DeterministicScenarioAssertionKind.FinalEquipmentState)
        {
            if (string.IsNullOrWhiteSpace(assertion.TargetId))
            {
                errors.Add($"Assertion '{assertion.AssertionId}' TargetId is required.");
            }
            if (string.IsNullOrWhiteSpace(assertion.ExpectedState))
            {
                errors.Add($"Assertion '{assertion.AssertionId}' ExpectedState is required.");
            }
        }

        return errors;
    }
}

public sealed record DeterministicScenarioAssertionOutcome(
    string AssertionId,
    DeterministicScenarioAssertionKind Kind,
    string? TargetId,
    string ExpectedValue,
    string ActualValue,
    long MinimumCount,
    bool IsPassed,
    long ObservedTickIndex,
    string Detail);

internal static class DeterministicScenarioAssertionEvaluator
{
    public static ImmutableArray<DeterministicScenarioAssertionOutcome> Evaluate(
        ImmutableArray<DeterministicScenarioAssertion> assertions,
        IReadOnlyList<SimulationSnapshot> snapshots,
        IReadOnlyList<SimulationEvent> events)
    {
        if (assertions.IsDefaultOrEmpty)
        {
            return ImmutableArray<DeterministicScenarioAssertionOutcome>.Empty;
        }

        SimulationSnapshot? finalSnapshot = snapshots.LastOrDefault();
        long finalTick = finalSnapshot?.TickIndex ?? 0;
        var outcomes = ImmutableArray.CreateBuilder<DeterministicScenarioAssertionOutcome>(
            assertions.Length);
        foreach (var assertion in assertions)
        {
            outcomes.Add(assertion.Kind switch
            {
                DeterministicScenarioAssertionKind.AutomaticCycleCompleted =>
                    EvaluateAutomaticCycles(assertion, events, finalTick),
                DeterministicScenarioAssertionKind.NoActiveFaults =>
                    EvaluateFinalFaults(assertion, finalSnapshot, finalTick),
                DeterministicScenarioAssertionKind.FinalEquipmentState =>
                    EvaluateFinalEquipmentState(assertion, finalSnapshot, finalTick),
                _ => throw new InvalidOperationException(
                    $"Unsupported scenario assertion kind '{assertion.Kind}'.")
            });
        }

        return outcomes.ToImmutable();
    }

    public static string HashDefinitions(
        IEnumerable<DeterministicScenarioAssertion> assertions)
    {
        var builder = new StringBuilder();
        foreach (var assertion in assertions)
        {
            builder.Append(assertion.AssertionId).Append('|')
                .Append(assertion.Kind).Append('|')
                .Append(assertion.TargetId).Append('|')
                .Append(assertion.ExpectedState).Append('|')
                .Append(assertion.MinimumCount).Append('\n');
        }

        return Hash(builder.ToString());
    }

    public static string HashDefinitions(
        IEnumerable<DeterministicScenarioAssertionOutcome> outcomes) =>
        HashDefinitions(outcomes.Select(outcome => new DeterministicScenarioAssertion(
            outcome.AssertionId,
            outcome.Kind,
            outcome.TargetId,
            outcome.Kind == DeterministicScenarioAssertionKind.FinalEquipmentState
                ? outcome.ExpectedValue
                : null,
            outcome.MinimumCount)));

    public static string HashOutcomes(
        IEnumerable<DeterministicScenarioAssertionOutcome> outcomes)
    {
        var builder = new StringBuilder();
        foreach (var outcome in outcomes)
        {
            builder.Append(outcome.AssertionId).Append('|')
                .Append(outcome.Kind).Append('|')
                .Append(outcome.TargetId).Append('|')
                .Append(outcome.ExpectedValue).Append('|')
                .Append(outcome.ActualValue).Append('|')
                .Append(outcome.MinimumCount).Append('|')
                .Append(outcome.IsPassed).Append('|')
                .Append(outcome.ObservedTickIndex).Append('|')
                .Append(outcome.Detail).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static DeterministicScenarioAssertionOutcome EvaluateAutomaticCycles(
        DeterministicScenarioAssertion assertion,
        IReadOnlyList<SimulationEvent> events,
        long finalTick)
    {
        SimulationEvent[] completed = events
            .Where(item => item.Code == "AutomaticRunCycleCompleted")
            .ToArray();
        bool passed = completed.LongLength >= assertion.MinimumCount;
        long observedTick = passed
            ? completed[(int)assertion.MinimumCount - 1].TickIndex
            : finalTick;
        string expected = $">={assertion.MinimumCount.ToString(CultureInfo.InvariantCulture)}";
        string actual = completed.LongLength.ToString(CultureInfo.InvariantCulture);
        return Outcome(
            assertion,
            expected,
            actual,
            passed,
            observedTick,
            passed
                ? $"Observed {actual} completed automatic cycle event(s)."
                : $"Expected {expected} completed automatic cycle event(s), observed {actual}.");
    }

    private static DeterministicScenarioAssertionOutcome EvaluateFinalFaults(
        DeterministicScenarioAssertion assertion,
        SimulationSnapshot? finalSnapshot,
        long finalTick)
    {
        int count = finalSnapshot?.Faults.Count ?? 0;
        bool passed = finalSnapshot is not null && count == 0;
        string actual = finalSnapshot is null
            ? "Unavailable"
            : count.ToString(CultureInfo.InvariantCulture);
        return Outcome(
            assertion,
            "0",
            actual,
            passed,
            finalTick,
            passed ? "Final snapshot has no active faults." : $"Final active fault count is {actual}.");
    }

    private static DeterministicScenarioAssertionOutcome EvaluateFinalEquipmentState(
        DeterministicScenarioAssertion assertion,
        SimulationSnapshot? finalSnapshot,
        long finalTick)
    {
        string actual = ResolveEquipmentState(finalSnapshot, assertion.TargetId!) ?? "Unavailable";
        string expected = assertion.ExpectedState!;
        bool passed = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return Outcome(
            assertion,
            expected,
            actual,
            passed,
            finalTick,
            passed
                ? $"Equipment '{assertion.TargetId}' finished in state '{actual}'."
                : $"Equipment '{assertion.TargetId}' expected state '{expected}', observed '{actual}'.");
    }

    internal static string? ResolveEquipmentState(
        SimulationSnapshot? snapshot,
        string targetId)
    {
        if (snapshot is null)
        {
            return null;
        }

        var axis = snapshot.Axes.SingleOrDefault(item => item.Id == targetId);
        if (axis is not null)
        {
            return axis.State.ToString();
        }

        var component = snapshot.LayoutComponents.SingleOrDefault(item => item.Id == targetId);
        if (component is not null)
        {
            if (component.CylinderState.HasValue)
            {
                return component.CylinderState.Value.ToString();
            }
            if (component.ConveyorRunning.HasValue)
            {
                return component.ConveyorRunning.Value
                    ? $"{component.ConveyorDirection}Running"
                    : "Stopped";
            }
            if (component.IsDetected.HasValue)
            {
                return component.IsDetected.Value ? "Detected" : "Clear";
            }
            if (component.InspectionState.HasValue)
            {
                return component.InspectionState.Value.ToString();
            }
        }

        var loadLock = snapshot.LoadLocks.SingleOrDefault(item => item.Id == targetId);
        if (loadLock is not null)
        {
            return loadLock.State.ToString();
        }

        return snapshot.Workpieces.SingleOrDefault(item => item.Id == targetId)?.State.ToString();
    }

    private static DeterministicScenarioAssertionOutcome Outcome(
        DeterministicScenarioAssertion assertion,
        string expectedValue,
        string actualValue,
        bool isPassed,
        long observedTickIndex,
        string detail) =>
        new(
            assertion.AssertionId,
            assertion.Kind,
            assertion.TargetId,
            expectedValue,
            actualValue,
            assertion.MinimumCount,
            isPassed,
            observedTickIndex,
            detail);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
