using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SimulationEvidenceRetentionPolicyTests
{
    [Fact]
    public void Plan_AgeAndByteLimitsSelectOldestAppendOnlyExportsAndKeepProtectedArtifacts()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var artifacts = new[]
        {
            Artifact("project.ovmachine.batch-result.json", SimulationEvidenceArtifactStorageMode.FixedSidecar, 100, now.AddDays(-30)),
            Artifact("accepted-baseline.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 90, now.AddDays(-30), isAcceptedBaseline: true),
            Artifact("current-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 80, now.AddDays(-30), isCurrentReference: true),
            Artifact("old-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 6, now.AddDays(-3)),
            Artifact("recent-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 6, now.AddHours(-1)),
            Artifact("new-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 6, now)
        };

        var plan = SimulationEvidenceRetentionPlanner.Plan(
            artifacts,
            new SimulationEvidenceRetentionPolicy(
                MaximumAppendOnlyExportAge: TimeSpan.FromDays(1),
                MaximumAppendOnlyExportBytes: 182),
            now,
            availableFreeBytes: 1_000);

        Assert.True(plan.IsSatisfiable);
        Assert.Equal(["old-export.json"], plan.DeletionCandidates.Select(item => Path.GetFileName(item.Path)));
        Assert.Contains(plan.RetainedArtifacts, item => item.Path.EndsWith("project.ovmachine.batch-result.json", StringComparison.Ordinal));
        Assert.Contains(plan.RetainedArtifacts, item => item.IsAcceptedBaseline);
        Assert.Contains(plan.RetainedArtifacts, item => item.IsCurrentReference);
        Assert.Equal(288, plan.TotalBytes);
        Assert.Equal(282, plan.RetainedBytes);
    }

    [Fact]
    public void Plan_DistinguishesFixedSidecarFromAppendOnlyExportBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = SimulationEvidenceRetentionPlanner.Plan(
            [
                Artifact("fixed-sidecar.json", SimulationEvidenceArtifactStorageMode.FixedSidecar, 100, now.AddDays(-10)),
                Artifact("export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 10, now.AddDays(-10))
            ],
            new SimulationEvidenceRetentionPolicy(MaximumAppendOnlyExportBytes: 1),
            now,
            availableFreeBytes: 1_000);

        Assert.True(plan.IsSatisfiable);
        Assert.Single(plan.DeletionCandidates);
        Assert.EndsWith("export.json", plan.DeletionCandidates[0].Path, StringComparison.Ordinal);
        Assert.Single(plan.RetainedArtifacts);
        Assert.EndsWith("fixed-sidecar.json", plan.RetainedArtifacts[0].Path, StringComparison.Ordinal);
        Assert.Equal(110, plan.TotalBytes);
        Assert.Equal(100, plan.RetainedBytes);
    }

    [Fact]
    public void Plan_CountLimitRemovesOldestUnprotectedExportsAndKeepsBaseline()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = SimulationEvidenceRetentionPlanner.Plan(
            [
                Artifact("old-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now.AddDays(-3)),
                Artifact("recent-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now.AddDays(-2)),
                Artifact("accepted-baseline.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now.AddDays(-4), isAcceptedBaseline: true)
            ],
            new SimulationEvidenceRetentionPolicy(MaximumAppendOnlyExportCount: 1),
            now,
            availableFreeBytes: 1_000);

        Assert.True(plan.IsSatisfiable);
        Assert.Equal(["old-export.json", "recent-export.json"], plan.DeletionCandidates.Select(item => Path.GetFileName(item.Path)));
        Assert.Single(plan.RetainedArtifacts);
        Assert.True(plan.RetainedArtifacts[0].IsAcceptedBaseline);
    }

    [Fact]
    public void Plan_LeavesExportAtExactAgeBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = SimulationEvidenceRetentionPlanner.Plan(
            [Artifact("boundary-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now.AddDays(-1))],
            new SimulationEvidenceRetentionPolicy(MaximumAppendOnlyExportAge: TimeSpan.FromDays(1)),
            now,
            availableFreeBytes: 1_000);

        Assert.True(plan.IsSatisfiable);
        Assert.Empty(plan.DeletionCandidates);
        Assert.Single(plan.RetainedArtifacts);
    }

    [Fact]
    public void Plan_CombinedByteAndCountLimitsDoNotOverDelete()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = SimulationEvidenceRetentionPlanner.Plan(
            [
                Artifact("large-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 100, now.AddDays(-3)),
                Artifact("recent-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now.AddDays(-2)),
                Artifact("new-export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now.AddDays(-1))
            ],
            new SimulationEvidenceRetentionPolicy(
                MaximumAppendOnlyExportBytes: 101,
                MaximumAppendOnlyExportCount: 2),
            now,
            availableFreeBytes: 1_000);

        Assert.True(plan.IsSatisfiable);
        Assert.Equal(["large-export.json"], plan.DeletionCandidates.Select(item => Path.GetFileName(item.Path)));
        Assert.Equal(2, plan.RetainedArtifacts.Length);
    }

    [Fact]
    public void Plan_ReportsDiskShortfallWhenProtectedAndUnprotectedBytesCannotReachFloor()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = SimulationEvidenceRetentionPlanner.Plan(
            [
                Artifact("baseline.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 50, now.AddDays(-3), isAcceptedBaseline: true),
                Artifact("export.json", SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 20, now.AddDays(-2))
            ],
            new SimulationEvidenceRetentionPolicy(MinimumFreeBytes: 100),
            now,
            availableFreeBytes: 0);

        Assert.False(plan.IsSatisfiable);
        Assert.Contains("minimum free space", plan.UnsatisfiedConstraint, StringComparison.Ordinal);
        Assert.Single(plan.DeletionCandidates);
        Assert.Equal(20, plan.BytesToRelease);
    }

    [Fact]
    public void Plan_RejectsCorruptedIndexDuplicatePath()
    {
        var now = DateTimeOffset.UtcNow;
        var duplicate = Path.Combine(TestStorage.RootPath, "retention-index.json");

        Assert.Throws<InvalidOperationException>(() =>
            SimulationEvidenceRetentionPlanner.Plan(
                [
                    Artifact(duplicate, SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now),
                    Artifact(duplicate.ToUpperInvariant(), SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 1, now)
                ],
                new SimulationEvidenceRetentionPolicy(),
                now,
                availableFreeBytes: 1_000));
    }

    [Fact]
    public void Executor_RechecksCurrentReferenceAndFileSizeBeforeDeletion()
    {
        var directory = Path.Combine(
            TestStorage.RootPath,
            "retention-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "old-export.json");
        File.WriteAllText(path, "old");

        try
        {
            var now = DateTimeOffset.UtcNow;
            var plan = SimulationEvidenceRetentionPlanner.Plan(
                [Artifact(path, SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 3, now.AddDays(-2))],
                new SimulationEvidenceRetentionPolicy(MaximumAppendOnlyExportAge: TimeSpan.FromDays(1)),
                now,
                availableFreeBytes: 1_000);

            var protectedResult = SimulationEvidenceRetentionExecutor.Apply(
                plan,
                _ => 1_000,
                _ => true);
            Assert.False(protectedResult.IsComplete);
            Assert.True(File.Exists(path));

            File.WriteAllText(path, "changed");
            var changedResult = SimulationEvidenceRetentionExecutor.Apply(
                plan,
                _ => 1_000,
                _ => false);
            Assert.False(changedResult.IsComplete);
            Assert.True(File.Exists(path));

            File.WriteAllText(path, "old");
            var deletedResult = SimulationEvidenceRetentionExecutor.Apply(
                plan,
                _ => 1_000,
                _ => false);
            Assert.True(deletedResult.IsComplete, deletedResult.FailureReason);
            Assert.Equal(Path.GetFullPath(path), Assert.Single(deletedResult.DeletedPaths));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Executor_RejectsDiskFloorFailureWithoutDeletingFiles()
    {
        var directory = Path.Combine(
            TestStorage.RootPath,
            "retention-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "old-export.json");
        File.WriteAllText(path, "old");

        try
        {
            var now = DateTimeOffset.UtcNow;
            var plan = SimulationEvidenceRetentionPlanner.Plan(
                [Artifact(path, SimulationEvidenceArtifactStorageMode.AppendOnlyExport, 3, now.AddDays(-2))],
                new SimulationEvidenceRetentionPolicy(
                    MaximumAppendOnlyExportAge: TimeSpan.FromDays(1),
                    MinimumFreeBytes: 10),
                now,
                availableFreeBytes: 0);

            var result = SimulationEvidenceRetentionExecutor.Apply(plan, _ => 0);

            Assert.False(result.IsComplete);
            Assert.Empty(result.DeletedPaths);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SimulationEvidenceArtifactDescriptor Artifact(
        string path,
        SimulationEvidenceArtifactStorageMode storageMode,
        long sizeBytes,
        DateTimeOffset lastWriteTimeUtc,
        bool isCurrentReference = false,
        bool isAcceptedBaseline = false) =>
        new(
            path,
            storageMode,
            sizeBytes,
            lastWriteTimeUtc,
            isCurrentReference,
            isAcceptedBaseline);
}
