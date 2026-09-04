using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SmokeLayoutAlignmentVerifierTests
{
    private static string SamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "AutomaticTransferCell.ovmachine");

    [Fact]
    public void VerifiesAllAlignmentModesAndGroupedNudgeForRotatedSelection()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        var layout = new MachineLayoutViewModel();
        layout.Load(project);
        layout.Items.Single(item => item.Id == "sensor-1").CurrentRotationDegrees = 30;

        var requestedIds = new[] { "stage-1", "sensor-1", "cylinder-1" };
        var report = SmokeLayoutAlignmentVerifier.Verify(
            layout,
            requestedIds,
            nameof(LayoutSelectionAlignment.Bottom));

        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Failures));
        Assert.Equal(requestedIds, report.RequestedIds);
        Assert.True(report.SelectedIds.ToHashSet(StringComparer.Ordinal).SetEquals(requestedIds));
        Assert.Equal("cylinder-1", report.PrimaryId);
        Assert.Equal(nameof(LayoutSelectionAlignment.Bottom), report.FinalAlignment);
        Assert.Equal(Enum.GetValues<LayoutSelectionAlignment>().Length, report.MaximumDeviationByAlignment.Count);
        Assert.All(report.MaximumDeviationByAlignment.Values, deviation => Assert.InRange(deviation, 0, 0.000001d));
        Assert.InRange(report.MaximumNudgeDeviation, 0, 0.000001d);
    }

    [Fact]
    public void ReportsMissingRequestedItemsWithoutChangingFailureContract()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        var layout = new MachineLayoutViewModel();
        layout.Load(project);

        var report = SmokeLayoutAlignmentVerifier.Verify(
            layout,
            ["stage-1", "missing-item"],
            nameof(LayoutSelectionAlignment.HorizontalCenter));

        Assert.False(report.IsValid);
        Assert.Contains("Requested 2 items but found 1.", report.Failures);
        Assert.Equal("stage-1", report.PrimaryId);
    }

    [Fact]
    public void RejectsUnsupportedAlignmentValueAtTheExistingBoundary()
    {
        var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
        var layout = new MachineLayoutViewModel();
        layout.Load(project);

        var exception = Assert.Throws<ArgumentException>(() =>
            SmokeLayoutAlignmentVerifier.Verify(layout, ["stage-1", "sensor-1"], "Diagonal"));

        Assert.Equal("Unsupported --smoke-layout-align 'Diagonal'.", exception.Message);
    }
}
