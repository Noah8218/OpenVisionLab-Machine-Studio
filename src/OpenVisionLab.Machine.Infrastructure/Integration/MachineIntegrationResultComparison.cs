using System.Collections.ObjectModel;
using System.Globalization;
using OpenVisionLab.Integration.Contracts;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

public enum MachineIntegrationResultComparisonStatus
{
    Match,
    WithinTolerance,
    Regression,
    ComparisonUnavailable
}

public enum MachineIntegrationResultDifferenceKind
{
    RecordCount,
    RunId,
    ShotId,
    InputHash,
    RecipeHash,
    RecipeVersion,
    Judgment,
    MetricMissing,
    MetricUnit,
    MetricValue
}

/// <summary>
/// Explicit, versioned metric tolerance policy for external-result comparison.
/// A null policy keeps the comparison exact. Invalid values are retained as an
/// invalid policy so the comparison can fail closed instead of throwing from a
/// result-observation path.
/// </summary>
public sealed class MachineIntegrationResultToleranceProfile
{
    public MachineIntegrationResultToleranceProfile(
        string? version,
        IReadOnlyDictionary<string, double>? absoluteMetricTolerances)
    {
        Version = version?.Trim() ?? string.Empty;
        var copied = new Dictionary<string, double>(StringComparer.Ordinal);
        var isValid = !string.IsNullOrWhiteSpace(Version)
            && absoluteMetricTolerances is not null;
        if (absoluteMetricTolerances is not null)
        {
            foreach (var pair in absoluteMetricTolerances)
            {
                var name = pair.Key?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)
                    || !string.Equals(name, pair.Key, StringComparison.Ordinal)
                    || !double.IsFinite(pair.Value)
                    || pair.Value < 0
                    || !copied.TryAdd(name, pair.Value))
                {
                    isValid = false;
                    continue;
                }

            }
        }

        AbsoluteMetricTolerances = new ReadOnlyDictionary<string, double>(copied);
        IsValid = isValid;
    }

    public string Version { get; }

    public IReadOnlyDictionary<string, double> AbsoluteMetricTolerances { get; }

    public bool IsValid { get; }

    public bool TryGetTolerance(string metricName, out double tolerance) =>
        AbsoluteMetricTolerances.TryGetValue(metricName, out tolerance);
}

/// <summary>
/// Immutable comparison projection of one already validated external Result.
/// Recipe version is deliberately caller-supplied because the shared V2
/// contract carries recipe content identity, not a product-version field.
/// </summary>
public sealed class MachineIntegrationResultComparisonInput
{
    public MachineIntegrationResultComparisonInput(
        string? runId,
        string? shotId,
        string? inputSha256,
        string? recipeSha256,
        string? recipeVersion,
        IntegrationInspectionOutcome judgment,
        IntegrationResultStatus status,
        IReadOnlyList<IntegrationMetric>? metrics)
    {
        RunId = runId?.Trim() ?? string.Empty;
        ShotId = shotId?.Trim() ?? string.Empty;
        InputSha256 = inputSha256?.Trim() ?? string.Empty;
        RecipeSha256 = recipeSha256?.Trim() ?? string.Empty;
        RecipeVersion = recipeVersion?.Trim();
        Judgment = judgment;
        Status = status;
        Metrics = Array.AsReadOnly(metrics?.ToArray() ?? []);
    }

    public static MachineIntegrationResultComparisonInput FromResult(
        IntegrationResultV2 result,
        string? recipeVersion)
    {
        ArgumentNullException.ThrowIfNull(result);
        var correlation = result.Correlation
            ?? throw new ArgumentException("A Result correlation is required.", nameof(result));

        return new(
            result.RunId,
            correlation.AcquisitionId,
            correlation.InputSha256,
            correlation.RecipeSha256,
            recipeVersion,
            result.Outcome,
            result.Status,
            result.Metrics);
    }

    public string RunId { get; }

    public string ShotId { get; }

    public string InputSha256 { get; }

    public string RecipeSha256 { get; }

    public string? RecipeVersion { get; }

    public IntegrationInspectionOutcome Judgment { get; }

    public IntegrationResultStatus Status { get; }

    public IReadOnlyList<IntegrationMetric> Metrics { get; }
}

public sealed record MachineIntegrationResultFirstMismatch(
    MachineIntegrationResultDifferenceKind Kind,
    string RunId,
    string ShotId,
    string InputSha256,
    string? MetricName,
    string? Unit,
    string? ExpectedValue,
    string? ActualValue,
    double? AbsoluteDifference,
    double? AllowedTolerance);

public sealed record MachineIntegrationResultComparison(
    MachineIntegrationResultComparisonStatus Status,
    string? ToleranceProfileVersion,
    MachineIntegrationResultFirstMismatch? FirstMismatch,
    string? Detail)
{
    public bool IsComparable => Status != MachineIntegrationResultComparisonStatus.ComparisonUnavailable;

    public bool IsMatch => Status is MachineIntegrationResultComparisonStatus.Match
        or MachineIntegrationResultComparisonStatus.WithinTolerance;
}

/// <summary>
/// Compares explicit external inspection observations without publishing,
/// applying, or mutating a transaction. The list overload is the bounded
/// Run/Shot comparison owner; it returns the first deterministic difference.
/// </summary>
public static class MachineIntegrationResultComparator
{
    public static MachineIntegrationResultComparison Compare(
        MachineIntegrationResultComparisonInput expected,
        MachineIntegrationResultComparisonInput actual,
        MachineIntegrationResultToleranceProfile? toleranceProfile = null) =>
        Compare([expected], [actual], toleranceProfile);

    public static MachineIntegrationResultComparison Compare(
        IReadOnlyList<MachineIntegrationResultComparisonInput>? expected,
        IReadOnlyList<MachineIntegrationResultComparisonInput>? actual,
        MachineIntegrationResultToleranceProfile? toleranceProfile = null)
    {
        var profileVersion = toleranceProfile?.Version;
        if (toleranceProfile is { IsValid: false })
        {
            return Unavailable(
                profileVersion,
                "The tolerance profile is missing a version or contains an invalid metric tolerance.");
        }

        if (expected is null || actual is null || expected.Count == 0 || actual.Count == 0)
        {
            return Unavailable(profileVersion, "At least one expected and actual Result is required.");
        }

        if (expected.Count != actual.Count)
        {
            return new(
                MachineIntegrationResultComparisonStatus.ComparisonUnavailable,
                profileVersion,
                new(
                    MachineIntegrationResultDifferenceKind.RecordCount,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    null,
                    expected.Count.ToString(CultureInfo.InvariantCulture),
                    actual.Count.ToString(CultureInfo.InvariantCulture),
                    null,
                    null),
                "Expected and actual Result counts differ.");
        }

        var expectedValidation = Validate(expected, profileVersion);
        if (expectedValidation is not null)
        {
            return expectedValidation;
        }

        var actualValidation = Validate(actual, profileVersion);
        if (actualValidation is not null)
        {
            return actualValidation;
        }

        var expectedByKey = expected
            .GroupBy(item => (item.RunId, item.ShotId))
            .ToArray();
        var actualByKey = actual
            .GroupBy(item => (item.RunId, item.ShotId))
            .ToArray();
        if (expectedByKey.Any(group => group.Count() != 1)
            || actualByKey.Any(group => group.Count() != 1))
        {
            return Unavailable(profileVersion, "Run/Shot identities must be unique within each result set.");
        }

        var orderedExpected = expected
            .OrderBy(item => item.RunId, StringComparer.Ordinal)
            .ThenBy(item => item.ShotId, StringComparer.Ordinal)
            .ToArray();
        var orderedActual = actual
            .OrderBy(item => item.RunId, StringComparer.Ordinal)
            .ThenBy(item => item.ShotId, StringComparer.Ordinal)
            .ToArray();
        MachineIntegrationResultFirstMismatch? firstDifference = null;
        MachineIntegrationResultFirstMismatch? firstRegression = null;

        for (var index = 0; index < orderedExpected.Length; index++)
        {
            var expectedItem = orderedExpected[index];
            var actualItem = orderedActual[index];
            var pair = ComparePair(expectedItem, actualItem, toleranceProfile);
            if (pair.Status == MachineIntegrationResultComparisonStatus.ComparisonUnavailable)
            {
                return pair with { ToleranceProfileVersion = profileVersion };
            }

            if (pair.FirstMismatch is not null && firstDifference is null)
            {
                firstDifference = pair.FirstMismatch;
            }

            if (pair.Status == MachineIntegrationResultComparisonStatus.Regression
                && firstRegression is null)
            {
                firstRegression = pair.FirstMismatch;
            }
        }

        if (firstRegression is not null)
        {
            return new(
                MachineIntegrationResultComparisonStatus.Regression,
                profileVersion,
                firstRegression,
                "The first external Result difference is outside the accepted comparison contract.");
        }

        return firstDifference is null
            ? new(MachineIntegrationResultComparisonStatus.Match, profileVersion, null, null)
            : new(
                MachineIntegrationResultComparisonStatus.WithinTolerance,
                profileVersion,
                firstDifference,
                "Metric differences are within the explicit tolerance profile.");
    }

    private static MachineIntegrationResultComparison? Validate(
        IReadOnlyList<MachineIntegrationResultComparisonInput> values,
        string? profileVersion)
    {
        foreach (var item in values)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.RunId)
                || string.IsNullOrWhiteSpace(item.ShotId)
                || !IsSha256(item.InputSha256)
                || !IsSha256(item.RecipeSha256)
                || string.IsNullOrWhiteSpace(item.RecipeVersion)
                || item.Status != IntegrationResultStatus.Completed
                || !Enum.IsDefined(item.Judgment)
                || item.Metrics.Count == 0)
            {
                return Unavailable(profileVersion, "A Result is missing required comparison identity, completion, recipe version, or metrics.");
            }

            var seenMetricNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var metric in item.Metrics)
            {
                if (metric is null
                    || string.IsNullOrWhiteSpace(metric.Name)
                    || !string.Equals(metric.Name, metric.Name.Trim(), StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(metric.Unit)
                    || !string.Equals(metric.Unit, metric.Unit.Trim(), StringComparison.Ordinal)
                    || !double.IsFinite(metric.Value)
                    || !seenMetricNames.Add(metric.Name))
                {
                    return Unavailable(profileVersion, "A Result contains a missing, duplicate, or non-finite metric.");
                }
            }
        }

        return null;
    }

    private static MachineIntegrationResultComparison ComparePair(
        MachineIntegrationResultComparisonInput expected,
        MachineIntegrationResultComparisonInput actual,
        MachineIntegrationResultToleranceProfile? toleranceProfile)
    {
        if (!string.Equals(expected.RunId, actual.RunId, StringComparison.Ordinal))
        {
            return Regression(expected, actual, MachineIntegrationResultDifferenceKind.RunId, null, null, expected.RunId, actual.RunId, null, null);
        }
        if (!string.Equals(expected.ShotId, actual.ShotId, StringComparison.Ordinal))
        {
            return Regression(expected, actual, MachineIntegrationResultDifferenceKind.ShotId, null, null, expected.ShotId, actual.ShotId, null, null);
        }
        if (!string.Equals(expected.InputSha256, actual.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Regression(expected, actual, MachineIntegrationResultDifferenceKind.InputHash, null, null, expected.InputSha256, actual.InputSha256, null, null);
        }
        if (!string.Equals(expected.RecipeSha256, actual.RecipeSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Regression(expected, actual, MachineIntegrationResultDifferenceKind.RecipeHash, null, null, expected.RecipeSha256, actual.RecipeSha256, null, null);
        }
        if (!string.Equals(expected.RecipeVersion, actual.RecipeVersion, StringComparison.Ordinal))
        {
            return Regression(expected, actual, MachineIntegrationResultDifferenceKind.RecipeVersion, null, null, expected.RecipeVersion, actual.RecipeVersion, null, null);
        }
        if (expected.Judgment != actual.Judgment)
        {
            return Regression(expected, actual, MachineIntegrationResultDifferenceKind.Judgment, null, null, expected.Judgment.ToString(), actual.Judgment.ToString(), null, null);
        }

        var expectedMetrics = expected.Metrics
            .OrderBy(metric => metric.Name, StringComparer.Ordinal)
            .ToArray();
        var actualMetrics = actual.Metrics
            .OrderBy(metric => metric.Name, StringComparer.Ordinal)
            .ToArray();
        if (!expectedMetrics.Select(metric => metric.Name).SequenceEqual(
                actualMetrics.Select(metric => metric.Name),
                StringComparer.Ordinal))
        {
            return Unavailable(
                toleranceProfile?.Version,
                "Expected and actual metric sets differ.") with
            {
                FirstMismatch = new(
                    MachineIntegrationResultDifferenceKind.MetricMissing,
                    actual.RunId,
                    actual.ShotId,
                    actual.InputSha256,
                    FirstMissingMetricName(expectedMetrics, actualMetrics),
                    null,
                    string.Join(",", expectedMetrics.Select(metric => metric.Name)),
                    string.Join(",", actualMetrics.Select(metric => metric.Name)),
                    null,
                    null)
            };
        }

        MachineIntegrationResultFirstMismatch? firstWithinTolerance = null;
        for (var index = 0; index < expectedMetrics.Length; index++)
        {
            var expectedMetric = expectedMetrics[index];
            var actualMetric = actualMetrics[index];
            if (!string.Equals(expectedMetric.Unit, actualMetric.Unit, StringComparison.Ordinal))
            {
                return Regression(expected, actual, MachineIntegrationResultDifferenceKind.MetricUnit, expectedMetric.Name, actualMetric.Unit, expectedMetric.Unit, actualMetric.Unit, null, null);
            }

            if (expectedMetric.Value == actualMetric.Value)
            {
                continue;
            }

            var difference = Math.Abs(actualMetric.Value - expectedMetric.Value);
            var tolerance = toleranceProfile is not null
                && toleranceProfile.TryGetTolerance(expectedMetric.Name, out var configuredTolerance)
                ? configuredTolerance
                : 0d;
            var mismatch = CreateDifference(
                MachineIntegrationResultDifferenceKind.MetricValue,
                expected,
                actual,
                expectedMetric.Name,
                actualMetric.Unit,
                Format(expectedMetric.Value),
                Format(actualMetric.Value),
                difference,
                tolerance);
            if (difference > tolerance)
            {
                return new(
                    MachineIntegrationResultComparisonStatus.Regression,
                    toleranceProfile?.Version,
                    mismatch,
                    "A metric difference exceeds the explicit tolerance profile.");
            }

            firstWithinTolerance ??= mismatch;
        }

        return firstWithinTolerance is null
            ? new(MachineIntegrationResultComparisonStatus.Match, toleranceProfile?.Version, null, null)
            : new(
                MachineIntegrationResultComparisonStatus.WithinTolerance,
                toleranceProfile?.Version,
                firstWithinTolerance,
                "Metric differences are within the explicit tolerance profile.");
    }

    private static MachineIntegrationResultComparison Regression(
        MachineIntegrationResultComparisonInput expected,
        MachineIntegrationResultComparisonInput actual,
        MachineIntegrationResultDifferenceKind kind,
        string? metricName,
        string? unit,
        string? expectedValue,
        string? actualValue,
        double? difference,
        double? tolerance) =>
        new(
            MachineIntegrationResultComparisonStatus.Regression,
            null,
            CreateDifference(
                kind,
                expected,
                actual,
                metricName,
                unit,
                expectedValue,
                actualValue,
                difference,
                tolerance),
            "The external Result identity or judgment differs.");

    private static MachineIntegrationResultFirstMismatch CreateDifference(
        MachineIntegrationResultDifferenceKind kind,
        MachineIntegrationResultComparisonInput expected,
        MachineIntegrationResultComparisonInput actual,
        string? metricName,
        string? unit,
        string? expectedValue,
        string? actualValue,
        double? difference,
        double? tolerance) =>
        new(
            kind,
            string.IsNullOrWhiteSpace(actual.RunId) ? expected.RunId : actual.RunId,
            string.IsNullOrWhiteSpace(actual.ShotId) ? expected.ShotId : actual.ShotId,
            string.IsNullOrWhiteSpace(actual.InputSha256) ? expected.InputSha256 : actual.InputSha256,
            metricName,
            unit,
            expectedValue,
            actualValue,
            difference,
            tolerance);

    private static MachineIntegrationResultComparison Unavailable(
        string? profileVersion,
        string detail) =>
        new(MachineIntegrationResultComparisonStatus.ComparisonUnavailable, profileVersion, null, detail);

    private static string? FirstMissingMetricName(
        IReadOnlyList<IntegrationMetric> expected,
        IReadOnlyList<IntegrationMetric> actual) =>
        expected.Select(metric => metric.Name)
            .Except(actual.Select(metric => metric.Name), StringComparer.Ordinal)
            .FirstOrDefault()
        ?? actual.Select(metric => metric.Name)
            .Except(expected.Select(metric => metric.Name), StringComparer.Ordinal)
            .FirstOrDefault();

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}
