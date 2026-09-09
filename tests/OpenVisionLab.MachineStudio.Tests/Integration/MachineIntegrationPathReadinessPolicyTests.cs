using OpenVisionLab.TestSupport;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationPathReadinessPolicyTests
{
    [Fact]
    public void EvaluateNormalizesPathsAndReportsCurrentAvailability()
    {
        using var fixture = new TestRoot();
        Directory.CreateDirectory(fixture.ExchangeRoot);
        File.WriteAllText(fixture.RecipePath, "{}");
        var policy = new MachineIntegrationPathReadinessPolicy();

        var readiness = policy.Evaluate(
            $"  {fixture.ExchangeRoot}  ",
            $"  {fixture.RecipePath}  ");

        Assert.Equal(fixture.ExchangeRoot, readiness.ExchangeRoot);
        Assert.Equal(fixture.RecipePath, readiness.InspectionRecipePath);
        Assert.True(readiness.IsExchangeRootAvailable);
        Assert.True(readiness.IsInspectionRecipeAvailable);
        Assert.True(readiness.CanRefreshResults);
        Assert.True(readiness.CanPublishHandoff);
    }

    [Fact]
    public void MissingOrBlankPathsFailClosed()
    {
        var policy = new MachineIntegrationPathReadinessPolicy();

        var readiness = policy.Evaluate(null, "  ");

        Assert.Equal(string.Empty, readiness.ExchangeRoot);
        Assert.Equal(string.Empty, readiness.InspectionRecipePath);
        Assert.False(readiness.IsExchangeRootAvailable);
        Assert.False(readiness.IsInspectionRecipeAvailable);
        Assert.False(readiness.CanRefreshResults);
        Assert.False(readiness.CanPublishHandoff);
    }

    [Fact]
    public void NormalizeFullPathPreservesEmptyInputAsEmpty()
    {
        using var fixture = new TestRoot();
        var policy = new MachineIntegrationPathReadinessPolicy();

        Assert.Equal(string.Empty, policy.NormalizeFullPath("  "));
        Assert.Equal(
            Path.GetFullPath(fixture.RecipePath),
            policy.NormalizeFullPath($"  {fixture.RecipePath}  "));
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Root = Path.Combine(
                TestStorage.RootPath,
                "machine-integration-path-readiness-policy-tests",
                Guid.NewGuid().ToString("N"));
            ExchangeRoot = Path.Combine(Root, "exchange");
            RecipePath = Path.Combine(Root, "recipe.json");
        }

        public string Root { get; }

        public string ExchangeRoot { get; }

        public string RecipePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
