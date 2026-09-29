using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicVisionExpectedJudgmentManifestTests
{
    [Fact]
    public void Create_SortsEntriesAndKeepsUnknownOrPendingOutOfScoring()
    {
        var manifest = DeterministicVisionExpectedJudgmentManifest.Create(
            "labels-2026-09-16.1",
            [
                Entry("sample-b", DeterministicVisionExpectedJudgment.Unknown,
                    DeterministicVisionLabelApprovalState.Approved),
                Entry("sample-a", DeterministicVisionExpectedJudgment.Pass,
                    DeterministicVisionLabelApprovalState.Approved),
                Entry("sample-c", DeterministicVisionExpectedJudgment.Fail,
                    DeterministicVisionLabelApprovalState.Pending)
            ]);

        Assert.True(manifest.HasValidManifestHash());
        Assert.Equal(["sample-a", "sample-b", "sample-c"],
            manifest.Entries.Select(entry => entry.SampleId));
        Assert.True(manifest.Entries[0].IsScorable);
        Assert.False(manifest.Entries[1].IsScorable);
        Assert.False(manifest.Entries[2].IsScorable);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsHashBoundManifest()
    {
        var manifest = DeterministicVisionExpectedJudgmentManifest.Create(
            "labels-1",
            [Entry("sample-1", DeterministicVisionExpectedJudgment.Fail,
                DeterministicVisionLabelApprovalState.Approved)]);
        var path = Path.Combine(TestStorage.RootPath, "mch-053-expected-judgment", "manifest.json");

        DeterministicVisionExpectedJudgmentManifest.SaveToJson(manifest, path);
        var restored = DeterministicVisionExpectedJudgmentManifest.LoadFromJson(path);

        Assert.NotNull(restored);
        Assert.True(restored!.HasValidManifestHash());
        Assert.Equal(
            DeterministicVisionExpectedJudgmentManifest.SaveToJson(manifest),
            DeterministicVisionExpectedJudgmentManifest.SaveToJson(restored));
    }

    [Fact]
    public void HasValidManifestHash_RejectsTamperingAndDuplicateSamples()
    {
        var manifest = DeterministicVisionExpectedJudgmentManifest.Create(
            "labels-1",
            [Entry("sample-1", DeterministicVisionExpectedJudgment.Pass,
                DeterministicVisionLabelApprovalState.Approved)]);

        Assert.False((manifest with { ManifestVersion = "labels-2" }).HasValidManifestHash());
        Assert.Throws<ArgumentException>(() =>
            DeterministicVisionExpectedJudgmentManifest.Create(
                "labels-1",
                [
                    Entry("sample-1", DeterministicVisionExpectedJudgment.Pass,
                        DeterministicVisionLabelApprovalState.Approved),
                    Entry("sample-1", DeterministicVisionExpectedJudgment.Fail,
                        DeterministicVisionLabelApprovalState.Approved)
                ]));
    }

    private static DeterministicVisionExpectedJudgmentEntry Entry(
        string sampleId,
        DeterministicVisionExpectedJudgment judgment,
        DeterministicVisionLabelApprovalState approvalState) =>
        new(
            sampleId,
            new string('A', 64),
            "recipe-1",
            "1.0.0",
            "label-1",
            judgment,
            "Reviewed against the deterministic sample contract.",
            "authorized-reviewer",
            approvalState);
}
