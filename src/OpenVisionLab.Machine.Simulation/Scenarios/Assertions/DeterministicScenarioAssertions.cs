using System.Collections.Immutable;
using OpenVisionLab.Machine.Core.Projects;

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
