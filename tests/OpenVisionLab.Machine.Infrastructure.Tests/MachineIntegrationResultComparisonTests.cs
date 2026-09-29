using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationResultComparisonTests
{
    [Fact]
    public void FromResult_UsesExistingRunShotCorrelationAndMetricContract()
    {
        var result = CreateExternalResult();

        var input = MachineIntegrationResultComparisonInput.FromResult(result, "recipe-7");

        Assert.Equal(result.RunId, input.RunId);
        Assert.Equal(result.Correlation.AcquisitionId, input.ShotId);
        Assert.Equal(result.Correlation.InputSha256, input.InputSha256);
        Assert.Equal(result.Correlation.RecipeSha256, input.RecipeSha256);
        Assert.Equal("recipe-7", input.RecipeVersion);
        Assert.Equal(result.Outcome, input.Judgment);
        Assert.Equal(result.Status, input.Status);
        Assert.Equal(result.Metrics, input.Metrics);
    }

    [Fact]
    public void ExactValues_ClassifyAsMatchWithoutToleranceProfile()
    {
        var expected = CreateInput();

        var comparison = MachineIntegrationResultComparator.Compare(expected, CreateInput());

        Assert.Equal(MachineIntegrationResultComparisonStatus.Match, comparison.Status);
        Assert.True(comparison.IsComparable);
        Assert.True(comparison.IsMatch);
        Assert.Null(comparison.FirstMismatch);
        Assert.Null(comparison.ToleranceProfileVersion);
    }

    [Fact]
    public void MetricDriftWithoutProfile_RemainsAnExactComparisonRegression()
    {
        var comparison = MachineIntegrationResultComparator.Compare(
            CreateInput(metricValue: 1.0),
            CreateInput(metricValue: 1.0001));

        Assert.Equal(MachineIntegrationResultComparisonStatus.Regression, comparison.Status);
        Assert.Equal(MachineIntegrationResultDifferenceKind.MetricValue, comparison.FirstMismatch?.Kind);
        Assert.Equal(0d, comparison.FirstMismatch?.AllowedTolerance);
    }

    [Fact]
    public void ExplicitProfile_ClassifiesFiniteMetricDriftInsideTolerance()
    {
        var expected = CreateInput(metricValue: 1.0);
        var actual = CreateInput(metricValue: 1.05);
        var profile = new MachineIntegrationResultToleranceProfile(
            "metric-v1",
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["Score"] = 0.1
            });

        var comparison = MachineIntegrationResultComparator.Compare(expected, actual, profile);

        Assert.Equal(MachineIntegrationResultComparisonStatus.WithinTolerance, comparison.Status);
        Assert.Equal("metric-v1", comparison.ToleranceProfileVersion);
        Assert.NotNull(comparison.FirstMismatch);
        Assert.Equal(MachineIntegrationResultDifferenceKind.MetricValue, comparison.FirstMismatch!.Kind);
        Assert.Equal(expected.InputSha256, comparison.FirstMismatch.InputSha256);
        Assert.Equal("1", comparison.FirstMismatch.ExpectedValue);
        Assert.Equal("1.05", comparison.FirstMismatch.ActualValue);
        Assert.Equal("mm", comparison.FirstMismatch.Unit);
        Assert.Equal(0.1, comparison.FirstMismatch.AllowedTolerance);
        Assert.True(comparison.IsMatch);
    }

    [Fact]
    public void OutOfToleranceMetric_ReportsFirstMismatchValuesAndInputHash()
    {
        var expected = CreateInput(metricValue: 1.0);
        var actual = CreateInput(metricValue: 1.25);
        var profile = new MachineIntegrationResultToleranceProfile(
            "metric-v1",
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["Score"] = 0.1
            });

        var comparison = MachineIntegrationResultComparator.Compare(expected, actual, profile);

        Assert.Equal(MachineIntegrationResultComparisonStatus.Regression, comparison.Status);
        Assert.False(comparison.IsMatch);
        Assert.NotNull(comparison.FirstMismatch);
        Assert.Equal(MachineIntegrationResultDifferenceKind.MetricValue, comparison.FirstMismatch!.Kind);
        Assert.Equal(actual.InputSha256, comparison.FirstMismatch.InputSha256);
        Assert.Equal("run-1", comparison.FirstMismatch.RunId);
        Assert.Equal("shot-1", comparison.FirstMismatch.ShotId);
        Assert.Equal("1", comparison.FirstMismatch.ExpectedValue);
        Assert.Equal("1.25", comparison.FirstMismatch.ActualValue);
        Assert.Equal(0.1, comparison.FirstMismatch.AllowedTolerance);
    }

    [Fact]
    public void IdentityAndJudgmentChanges_AreRegressionWithAStableFirstDifference()
    {
        var cases = new[]
        {
            (MachineIntegrationResultDifferenceKind.RunId, CreateInput(runId: "run-2")),
            (MachineIntegrationResultDifferenceKind.ShotId, CreateInput(shotId: "shot-2")),
            (MachineIntegrationResultDifferenceKind.InputHash, CreateInput(inputSha256: Hash('C'))),
            (MachineIntegrationResultDifferenceKind.RecipeHash, CreateInput(recipeSha256: Hash('D'))),
            (MachineIntegrationResultDifferenceKind.RecipeVersion, CreateInput(recipeVersion: "recipe-8")),
            (MachineIntegrationResultDifferenceKind.Judgment, CreateInput(judgment: IntegrationInspectionOutcome.Ng))
        };

        foreach (var (kind, actual) in cases)
        {
            var comparison = MachineIntegrationResultComparator.Compare(CreateInput(), actual);

            Assert.Equal(MachineIntegrationResultComparisonStatus.Regression, comparison.Status);
            Assert.NotNull(comparison.FirstMismatch);
            Assert.Equal(kind, comparison.FirstMismatch!.Kind);
            Assert.Equal(actual.InputSha256, comparison.FirstMismatch.InputSha256);
            Assert.NotNull(comparison.FirstMismatch.ExpectedValue);
            Assert.NotNull(comparison.FirstMismatch.ActualValue);
        }
    }

    [Fact]
    public void UnitOrMetricSetChanges_DoNotBecomeNumericTolerancePasses()
    {
        var expected = CreateInput();
        var unitChanged = CreateInput(metrics: [new IntegrationMetric("Score", 1.0, "px")]);
        var missingMetric = CreateInput(metrics: []);
        var extraMetric = CreateInput(
            metrics:
            [
                new IntegrationMetric("Score", 1.0, "mm"),
                new IntegrationMetric("Width", 12.0, "px")
            ]);
        var profile = new MachineIntegrationResultToleranceProfile(
            "metric-v1",
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["Score"] = 100
            });

        var unitComparison = MachineIntegrationResultComparator.Compare(expected, unitChanged, profile);
        var missingComparison = MachineIntegrationResultComparator.Compare(expected, missingMetric, profile);
        var extraComparison = MachineIntegrationResultComparator.Compare(expected, extraMetric, profile);

        Assert.Equal(MachineIntegrationResultComparisonStatus.Regression, unitComparison.Status);
        Assert.Equal(MachineIntegrationResultDifferenceKind.MetricUnit, unitComparison.FirstMismatch?.Kind);
        Assert.Equal(MachineIntegrationResultComparisonStatus.ComparisonUnavailable, missingComparison.Status);
        Assert.Equal(MachineIntegrationResultComparisonStatus.ComparisonUnavailable, extraComparison.Status);
        Assert.Equal(MachineIntegrationResultDifferenceKind.MetricMissing, extraComparison.FirstMismatch?.Kind);
    }

    [Fact]
    public void InvalidProfileOrObservation_IsComparisonUnavailable()
    {
        var invalidProfile = new MachineIntegrationResultToleranceProfile(
            version: null,
            absoluteMetricTolerances: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["Score"] = double.NaN
            });
        var missingRecipeVersion = CreateInput(recipeVersion: null);
        var invalidInputHash = CreateInput(inputSha256: "not-a-sha");
        var nonFiniteMetric = CreateInput(metrics: [new IntegrationMetric("Score", double.NaN, "mm")]);
        var duplicateMetric = CreateInput(
            metrics:
            [
                new IntegrationMetric("Score", 1.0, "mm"),
                new IntegrationMetric("Score", 1.0, "mm")
            ]);

        Assert.Equal(
            MachineIntegrationResultComparisonStatus.ComparisonUnavailable,
            MachineIntegrationResultComparator.Compare(CreateInput(), CreateInput(), invalidProfile).Status);
        Assert.Equal(
            MachineIntegrationResultComparisonStatus.ComparisonUnavailable,
            MachineIntegrationResultComparator.Compare(CreateInput(), missingRecipeVersion).Status);
        Assert.Equal(
            MachineIntegrationResultComparisonStatus.ComparisonUnavailable,
            MachineIntegrationResultComparator.Compare(CreateInput(), invalidInputHash).Status);
        Assert.Equal(
            MachineIntegrationResultComparisonStatus.ComparisonUnavailable,
            MachineIntegrationResultComparator.Compare(CreateInput(), nonFiniteMetric).Status);
        Assert.Equal(
            MachineIntegrationResultComparisonStatus.ComparisonUnavailable,
            MachineIntegrationResultComparator.Compare(CreateInput(), duplicateMetric).Status);
    }

    [Fact]
    public void EmptyOrMismatchedRunShotSets_AreComparisonUnavailable()
    {
        var expected = CreateInput();
        var countMismatch = MachineIntegrationResultComparator.Compare(
            [expected, CreateInput(runId: "run-2", shotId: "shot-2")],
            [expected]);
        var empty = MachineIntegrationResultComparator.Compare(
            Array.Empty<MachineIntegrationResultComparisonInput>(),
            [expected]);
        var duplicate = MachineIntegrationResultComparator.Compare(
            [expected, expected],
            [expected, CreateInput(runId: "run-2", shotId: "shot-2")]);

        Assert.Equal(MachineIntegrationResultComparisonStatus.ComparisonUnavailable, countMismatch.Status);
        Assert.Equal(MachineIntegrationResultDifferenceKind.RecordCount, countMismatch.FirstMismatch?.Kind);
        Assert.Equal(MachineIntegrationResultComparisonStatus.ComparisonUnavailable, empty.Status);
        Assert.Equal(MachineIntegrationResultComparisonStatus.ComparisonUnavailable, duplicate.Status);
    }

    private static MachineIntegrationResultComparisonInput CreateInput(
        string runId = "run-1",
        string shotId = "shot-1",
        string? inputSha256 = null,
        string? recipeSha256 = null,
        string? recipeVersion = "recipe-1",
        IntegrationInspectionOutcome judgment = IntegrationInspectionOutcome.Pass,
        IntegrationResultStatus status = IntegrationResultStatus.Completed,
        IReadOnlyList<IntegrationMetric>? metrics = null,
        double metricValue = 1.0) =>
        new(
            runId,
            shotId,
            inputSha256 ?? Hash('A'),
            recipeSha256 ?? Hash('B'),
            recipeVersion,
            judgment,
            status,
            metrics ?? [new IntegrationMetric("Score", metricValue, "mm")]);

    private static IntegrationResultV2 CreateExternalResult()
    {
        var consumer = new IntegrationApplicationIdentity(
            "OpenVisionLab.2DStudio",
            "1.0.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var correlation = new IntegrationRunCorrelation(
            "project-1",
            "1.12",
            "sequence-1",
            "step-1",
            "camera-1",
            "shot-1",
            "frame-1",
            "mm",
            IntegrationInspectionModality.TwoD,
            IntegrationInspectionInputKind.Image,
            Hash('A'),
            Hash('B'),
            consumer);
        return new(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Result,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            DateTimeOffset.UnixEpoch,
            consumer,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass,
            "run-1",
            new IntegrationArtifactReference(
                IntegrationArtifactRoles.RunRecord,
                "run-1",
                "artifacts/run-record.json",
                1,
                Hash('E')),
            correlation,
            [new IntegrationMetric("Score", 1.0, "mm")],
            [],
            null);
    }

    private static string Hash(char value) => new(value, 64);
}
