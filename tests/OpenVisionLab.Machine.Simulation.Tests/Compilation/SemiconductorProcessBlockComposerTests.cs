using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.Machine.Simulation.Sequences;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SemiconductorProcessBlockComposerTests
{
    private readonly ProjectDocumentStore _store = new();
    private readonly SemiconductorProcessBlockComposer _composer = new();

    [Fact]
    public async Task BranchedInspectionRecipe_ComposePreservesPassNgDecisionEdges()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "10-MetrologySorter.ovmachine");
        var project = _store.Load(File.ReadAllText(path));

        Assert.True(_composer.Apply(project, Enum.GetValues<SemiconductorProcessBlockKind>()).Changed);

        SequenceDefinition sequence = Assert.Single(project.Sequences);
        SequenceStepDefinition decision = sequence.Steps.Single(step => step.Id == "wait-metrology-result");
        Assert.Equal("start-pass-transport", decision.NextStepId);
        Assert.Equal("start-sort-transport", decision.FailureStepId);
        Assert.Equal("process-block.load.start", sequence.Steps[0].Id);
        Assert.Equal("process-block.align.move", sequence.Steps.Single(step =>
            step.Id == "cycle-done").NextStepId);
        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequence.Id);
        Assert.True(dryRun.IsCompleted, dryRun.Detail);
        Assert.Contains(dryRun.Timeline, trace => trace.StepId == "start-sort-transport");
        Assert.DoesNotContain(dryRun.Timeline, trace => trace.StepId == "start-pass-transport");
    }

    [Fact]
    public async Task AllFiveBlocks_ApplyOnceAndDryRunAcrossTenRecipes()
    {
        string[] paths = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(10, paths.Length);

        foreach (var path in paths)
        {
            var project = _store.Load(File.ReadAllText(path));
            int authoredStepCount = Assert.Single(project.Sequences).Steps.Count;
            int skippedAuthoredSteps = project.Devices.Any(device =>
                device.InspectionSortRouter is not null) ? 3 : 0;
            bool hasAuthoredOhtLoad = project.Devices.Any(device => device.OhtHandoff is not null);
            bool hasAuthoredInspection = project.Devices.Any(device => device.InspectionHandoff is not null);
            bool hasAuthoredPrealigner = project.Devices.Any(device => device.Prealigner is not null);
            int proposedManagedSteps = 13
                - (hasAuthoredOhtLoad ? 3 : 0)
                - (hasAuthoredInspection ? 1 : 0)
                - (hasAuthoredPrealigner ? 2 : 0);
            var kinds = Enum.GetValues<SemiconductorProcessBlockKind>().Reverse().ToArray();
            string beforePreview = _store.SerializeForEvidence(project);
            var preview = _composer.Preview(project, kinds);
            Assert.True(preview.CanApply, Path.GetFileName(path));
            Assert.Equal(Enum.GetValues<SemiconductorProcessBlockKind>(), preview.Kinds);
            Assert.Equal(proposedManagedSteps, preview.ProposedStepCount);
            Assert.Equal(beforePreview, _store.SerializeForEvidence(project));

            var applied = _composer.Apply(project, kinds);
            Assert.True(applied.Changed, Path.GetFileName(path));
            Assert.Equal(proposedManagedSteps, applied.AddedStepCount);

            string afterApply = _store.SerializeForEvidence(project);
            var repeated = _composer.Apply(project, kinds);
            Assert.False(repeated.Changed, $"{Path.GetFileName(path)} repeated");
            Assert.Equal(afterApply, _store.SerializeForEvidence(project));

            var retainedKinds = Enum.GetValues<SemiconductorProcessBlockKind>()
                .Where(kind => kind != SemiconductorProcessBlockKind.Inspect)
                .ToArray();
            var editPreview = _composer.Preview(project, retainedKinds);
            Assert.Equal(Enum.GetValues<SemiconductorProcessBlockKind>(), editPreview.ExistingKinds);
            Assert.Equal(hasAuthoredInspection ? 0 : 1, editPreview.RemovedStepCount);
            if (!hasAuthoredInspection)
            {
                Assert.Equal("process-block.inspect.confirm-position", Assert.Single(
                    editPreview.Steps,
                    step => step.Status == SemiconductorProcessBlockStepStatus.ProposedRemoval).StepId);
            }

            var edited = _composer.Apply(project, retainedKinds);
            Assert.Equal(!hasAuthoredInspection, edited.Changed);
            Assert.Equal(hasAuthoredInspection ? 0 : 1, edited.RemovedStepCount);
            Assert.DoesNotContain(project.Sequences.SelectMany(sequence => sequence.Steps), step =>
                step.Id.StartsWith("process-block.inspect.", StringComparison.Ordinal));

            var sequenceId = Assert.IsType<AutomaticRunDefinition>(project.Simulation.AutomaticRun).SequenceId;
            var result = await new DeterministicRecipeDryRunRunner().RunAsync(project, sequenceId);
            Assert.True(result.IsCompleted, $"{Path.GetFileName(path)}: {result.Detail}");
            Assert.Equal(authoredStepCount - skippedAuthoredSteps + proposedManagedSteps - (hasAuthoredInspection ? 0 : 1), result.Timeline.Count);
            Assert.Null(result.FirstIssue);

            var reopened = _store.Load(_store.Serialize(project));
            Assert.Equal(
                hasAuthoredInspection ? Enum.GetValues<SemiconductorProcessBlockKind>() : retainedKinds,
                _composer.RecognizeExistingKinds(reopened));
        }
    }

    [Theory]
    [InlineData(SemiconductorProcessBlockKind.Load, 3)]
    [InlineData(SemiconductorProcessBlockKind.Align, 2)]
    [InlineData(SemiconductorProcessBlockKind.Process, 4)]
    [InlineData(SemiconductorProcessBlockKind.Inspect, 1)]
    [InlineData(SemiconductorProcessBlockKind.Unload, 3)]
    public async Task BlankProject_AddsMissingStationAndSelectedRunnableBlock(
        SemiconductorProcessBlockKind kind,
        int expectedStepCount)
    {
        var project = new MachineProjectDocument
        {
            Name = "Blank process block",
            Simulation = new SimulationDefinition { FixedStepMilliseconds = 5 }
        };
        string before = _store.SerializeForEvidence(project);

        var preview = _composer.Preview(project, kind);
        Assert.Equal(10, preview.ProposedConnectionCount);
        Assert.Equal(expectedStepCount, preview.ProposedStepCount);
        Assert.Equal(before, _store.SerializeForEvidence(project));

        var applied = _composer.Apply(project, kind);
        Assert.True(applied.Changed);
        Assert.Equal(expectedStepCount, applied.AddedStepCount);
        Assert.NotNull(project.Simulation.AutomaticRun);

        var result = await new DeterministicRecipeDryRunRunner().RunAsync(
            project,
            project.Simulation.AutomaticRun!.SequenceId);
        Assert.True(result.IsCompleted, result.Detail);
        Assert.Null(result.FirstIssue);
    }

    [Fact]
    public void ConflictingManagedStep_BlocksApplyWithoutMutation()
    {
        string path = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(item => item, StringComparer.Ordinal)
            .Skip(1)
            .First();
        var project = _store.Load(File.ReadAllText(path));
        project.Sequences[0].Steps.Insert(0, new()
        {
            Id = "process-block.load.start",
            Name = "Conflicting step",
            Action = SequenceStepAction.SetSignal,
            TargetId = "do.cycle-active",
            Parameter = "false",
            NextStepId = project.Sequences[0].Steps[0].Id
        });
        string before = _store.SerializeForEvidence(project);

        var preview = _composer.Preview(project, SemiconductorProcessBlockKind.Load);
        var result = _composer.Apply(project, SemiconductorProcessBlockKind.Load);

        Assert.True(preview.UnavailableCount > 0);
        Assert.False(result.Changed);
        Assert.Equal(before, _store.SerializeForEvidence(project));
    }

    [Fact]
    public void MultiBlockPlan_LaterConflictAndEmptySelectionRemainAtomic()
    {
        string path = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(item => item, StringComparer.Ordinal)
            .Skip(1)
            .First();
        var project = _store.Load(File.ReadAllText(path));
        project.Sequences[0].Steps.Insert(project.Sequences[0].Steps.Count - 1, new()
        {
            Id = "process-block.unload.stop",
            Name = "Conflicting later step",
            Action = SequenceStepAction.SetSignal,
            TargetId = "do.cycle-done",
            Parameter = "true",
            NextStepId = "complete"
        });
        project.Sequences[0].Steps[^3].NextStepId = "process-block.unload.stop";
        string before = _store.SerializeForEvidence(project);

        var conflicted = _composer.Apply(
            project,
            Enum.GetValues<SemiconductorProcessBlockKind>());
        Assert.False(conflicted.Changed);
        Assert.True(conflicted.Preview.UnavailableCount > 0);
        Assert.Equal(before, _store.SerializeForEvidence(project));

        var empty = _composer.Apply(project, Array.Empty<SemiconductorProcessBlockKind>());
        Assert.False(empty.Changed);
        Assert.False(empty.Preview.CanApply);
        Assert.Equal(before, _store.SerializeForEvidence(project));
    }

    [Fact]
    public void CustomizedManagedStep_IsKeptWhileActionConflictStillBlocksApply()
    {
        string path = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(item => item, StringComparer.Ordinal)
            .Skip(1)
            .First();
        var project = _store.Load(File.ReadAllText(path));
        Assert.True(_composer.Apply(project, Enum.GetValues<SemiconductorProcessBlockKind>()).Changed);
        var sequence = project.Sequences.Single(item =>
            string.Equals(item.Id, project.Simulation.AutomaticRun!.SequenceId, StringComparison.Ordinal));
        var step = sequence.Steps.Single(item => item.Id == "process-block.inspect.confirm-position");
        step.TimeoutMs += 100;
        var moveStep = sequence.Steps.Single(item => item.Id == "process-block.align.move");
        moveStep.Parameter = "170";
        string customized = _store.SerializeForEvidence(project);

        var preview = _composer.Preview(project, Enum.GetValues<SemiconductorProcessBlockKind>());

        Assert.Equal(11, preview.ExistingStepCount);
        Assert.Equal(2, preview.CustomizedStepCount);
        Assert.Equal(0, preview.UnavailableCount);
        Assert.False(preview.CanApply);
        Assert.Equal(
            SemiconductorProcessBlockStepStatus.Customized,
            preview.Steps.Single(item => item.StepId == step.Id).Status);
        Assert.Equal(
            SemiconductorProcessBlockStepStatus.Customized,
            preview.Steps.Single(item => item.StepId == moveStep.Id).Status);
        Assert.Equal(customized, _store.SerializeForEvidence(project));

        var removal = _composer.Preview(
            project,
            Enum.GetValues<SemiconductorProcessBlockKind>()
                .Where(kind => kind != SemiconductorProcessBlockKind.Inspect));
        Assert.Equal(
            SemiconductorProcessBlockStepStatus.ProposedRemoval,
            removal.Steps.Single(item => item.StepId == step.Id).Status);
        Assert.Equal(0, removal.UnavailableCount);

        step.Action = SequenceStepAction.SetSignal;
        var conflict = _composer.Apply(project, Enum.GetValues<SemiconductorProcessBlockKind>());
        Assert.False(conflict.Changed);
        Assert.Equal(1, conflict.Preview.UnavailableCount);
        Assert.Equal(
            SemiconductorProcessBlockStepStatus.Unavailable,
            conflict.Preview.Steps.Single(item => item.StepId == step.Id).Status);
    }

    [Fact]
    public async Task FilteredManagedWaits_PreviewAndApplyAtomicallyAcrossTenRecipes()
    {
        string[] paths = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(10, paths.Length);

        foreach (var path in paths)
        {
            var project = _store.Load(File.ReadAllText(path));
            int authoredStepCount = Assert.Single(project.Sequences).Steps.Count;
            int skippedAuthoredSteps = project.Devices.Any(device =>
                device.InspectionSortRouter is not null) ? 3 : 0;
            bool hasAuthoredOhtLoad = project.Devices.Any(device => device.OhtHandoff is not null);
            bool hasAuthoredInspection = project.Devices.Any(device => device.InspectionHandoff is not null);
            bool hasAuthoredPrealigner = project.Devices.Any(device => device.Prealigner is not null);
            int managedWaitCount = 6
                - (hasAuthoredOhtLoad ? 1 : 0)
                - (hasAuthoredInspection ? 1 : 0)
                - (hasAuthoredPrealigner ? 1 : 0);
            int proposedManagedSteps = 13
                - (hasAuthoredOhtLoad ? 3 : 0)
                - (hasAuthoredInspection ? 1 : 0)
                - (hasAuthoredPrealigner ? 2 : 0);
            Assert.True(_composer.Apply(project, Enum.GetValues<SemiconductorProcessBlockKind>()).Changed);
            var sequence = project.Sequences.Single(item =>
                string.Equals(item.Id, project.Simulation.AutomaticRun!.SequenceId, StringComparison.Ordinal));
            var waitStepIds = sequence.Steps
                .Where(step => step.Id.StartsWith("process-block.", StringComparison.Ordinal)
                    && SemiconductorProcessBlockComposer.CanAdjustTimeout(step.Action))
                .Select(step => step.Id)
                .ToArray();
            Assert.Equal(managedWaitCount, waitStepIds.Length);
            string beforePreview = _store.SerializeForEvidence(project);

            var preview = _composer.PreviewTimeoutAdjustment(project, waitStepIds, 6000);

            Assert.True(preview.CanApply, Path.GetFileName(path));
            Assert.Equal(managedWaitCount, preview.ChangedCount);
            Assert.Empty(preview.InvalidStepIds);
            Assert.Equal(beforePreview, _store.SerializeForEvidence(project));

            var applied = _composer.ApplyTimeoutAdjustment(project, preview);

            Assert.True(applied.Changed, Path.GetFileName(path));
            Assert.Equal(managedWaitCount, applied.AppliedStepCount);
            var updatedSequence = project.Sequences.Single(item => item.Id == sequence.Id);
            Assert.All(waitStepIds, stepId => Assert.Equal(
                6000,
                updatedSequence.Steps.Single(step => step.Id == stepId).TimeoutMs));
            var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, updatedSequence.Id);
            Assert.True(
                dryRun.IsCompleted,
                $"{Path.GetFileName(path)}: {dryRun.FirstIssue?.StepId}: {dryRun.Detail}");
            Assert.Equal(authoredStepCount - skippedAuthoredSteps + proposedManagedSteps, dryRun.Timeline.Count);
        }
    }

    [Fact]
    public void ManagedWaitTimeout_StaleTargetAndInvalidInputLeaveProjectUnchanged()
    {
        string path = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(item => item, StringComparer.Ordinal)
            .First();
        var project = _store.Load(File.ReadAllText(path));
        Assert.True(_composer.Apply(project, Enum.GetValues<SemiconductorProcessBlockKind>()).Changed);
        var sequence = project.Sequences.Single(item =>
            string.Equals(item.Id, project.Simulation.AutomaticRun!.SequenceId, StringComparison.Ordinal));
        var step = sequence.Steps.Single(item => item.Id == "process-block.inspect.confirm-position");
        var preview = _composer.PreviewTimeoutAdjustment(project, [step.Id], 4500);
        Assert.True(preview.CanApply);

        step.TargetId = "changed-after-preview";
        string stale = _store.SerializeForEvidence(project);
        var rejected = _composer.ApplyTimeoutAdjustment(project, preview);

        Assert.False(rejected.Changed);
        Assert.Equal(0, rejected.AppliedStepCount);
        Assert.Equal(stale, _store.SerializeForEvidence(project));

        var invalid = _composer.PreviewTimeoutAdjustment(project, [step.Id], -1);
        string beforeInvalidApply = _store.SerializeForEvidence(project);
        var invalidResult = _composer.ApplyTimeoutAdjustment(project, invalid);
        Assert.False(invalid.CanApply);
        Assert.False(invalidResult.Changed);
        Assert.Equal(beforeInvalidApply, _store.SerializeForEvidence(project));
    }

    [Fact]
    public async Task ExistingPlan_RemovesOnlyExcludedManagedStepsAndPreservesAuthoredStep()
    {
        string path = Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "SemiconductorRecipes"),
                "*.ovmachine")
            .OrderBy(item => item, StringComparer.Ordinal)
            .First();
        var project = _store.Load(File.ReadAllText(path));
        Assert.True(_composer.Apply(project, Enum.GetValues<SemiconductorProcessBlockKind>()).Changed);
        var sequence = project.Sequences.Single(item =>
            string.Equals(item.Id, project.Simulation.AutomaticRun!.SequenceId, StringComparison.Ordinal));
        var sourceStep = sequence.Steps[0];
        var authoredStep = new SequenceStepDefinition
        {
            Id = "user-authored.hold",
            Name = "User authored hold",
            Action = sourceStep.Action,
            TargetId = sourceStep.TargetId,
            Parameter = sourceStep.Parameter,
            TimeoutMs = sourceStep.TimeoutMs
        };
        Assert.True(new SequenceDefinitionEditor().InsertBeforeTerminal(sequence, authoredStep).IsAccepted);

        var desiredKinds = Enum.GetValues<SemiconductorProcessBlockKind>()
            .Where(kind => kind != SemiconductorProcessBlockKind.Process)
            .ToArray();
        var equipmentCount = project.Devices.Count + project.Axes.Count;
        string beforePreview = _store.SerializeForEvidence(project);
        var preview = _composer.Preview(project, desiredKinds);
        Assert.Equal(4, preview.RemovedStepCount);
        Assert.Equal(beforePreview, _store.SerializeForEvidence(project));

        var result = _composer.Apply(project, desiredKinds);
        Assert.True(result.Changed);
        Assert.Equal(4, result.RemovedStepCount);
        var editedSequence = project.Sequences.Single(item => item.Id == sequence.Id);
        Assert.Contains(editedSequence.Steps, step => step.Id == authoredStep.Id);
        Assert.DoesNotContain(editedSequence.Steps, step =>
            step.Id.StartsWith("process-block.process.", StringComparison.Ordinal));
        Assert.Equal(equipmentCount, project.Devices.Count + project.Axes.Count);

        var dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, editedSequence.Id);
        Assert.True(dryRun.IsCompleted, dryRun.Detail);
        Assert.Equal(21, dryRun.Timeline.Count);

        var clearPreview = _composer.Preview(project, Array.Empty<SemiconductorProcessBlockKind>());
        Assert.Equal(6, clearPreview.RemovedStepCount);
        Assert.True(clearPreview.CanApply);
        var cleared = _composer.Apply(project, Array.Empty<SemiconductorProcessBlockKind>());
        Assert.True(cleared.Changed);
        Assert.Equal(6, cleared.RemovedStepCount);
        var clearedSequence = project.Sequences.Single(item => item.Id == editedSequence.Id);
        Assert.Contains(clearedSequence.Steps, step => step.Id == authoredStep.Id);
        Assert.DoesNotContain(clearedSequence.Steps, step =>
            step.Id.StartsWith("process-block.", StringComparison.Ordinal));
        Assert.Equal(equipmentCount, project.Devices.Count + project.Axes.Count);
        var clearedDryRun = await new DeterministicRecipeDryRunRunner().RunAsync(project, clearedSequence.Id);
        Assert.True(clearedDryRun.IsCompleted, clearedDryRun.Detail);
        Assert.Equal(15, clearedDryRun.Timeline.Count);
    }
}
