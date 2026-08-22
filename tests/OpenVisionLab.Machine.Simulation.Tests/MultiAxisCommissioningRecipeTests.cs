using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commissioning;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using System.Threading.Channels;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MultiAxisCommissioningRecipeTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task PersistedRecipe_RepeatValidationRoundTripsAndRejectsChangedContext()
    {
        MachineProjectDocument project = LoadSample();
        MultiAxisCommissioningRecipeDefinition recipe = Assert.IsType<MultiAxisCommissioningRecipeDefinition>(
            project.MultiAxisCommissioningRecipe);
        recipe.ValidationRepetitions = 3;
        string projectJson = new ProjectDocumentStore().SerializeForEvidence(project);
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);
        string projectPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ovmachine");
        string resultPath = $"{projectPath}.commissioning-result.json";
        try
        {
            var runner = new DeterministicMultiAxisCommissioningRunner();
            DeterministicMultiAxisCommissioningResultPackage first = await runner.RunAsync(
                runtime,
                project.Id,
                project.Name,
                projectPath,
                projectJson,
                recipe,
                FixedStep);
            DeterministicMultiAxisCommissioningResultPackage second = await runner.RunAsync(
                runtime,
                project.Id,
                project.Name,
                projectPath,
                projectJson,
                recipe,
                FixedStep);

            Assert.True(first.IsSuccess);
            Assert.True(first.HasValidEvidenceHash());
            Assert.Equal(3, first.CompletedRuns);
            Assert.All(first.Runs, run => Assert.True(run.IsMatch));
            Assert.Single(first.Runs.Select(run => run.SnapshotHash).Distinct(StringComparer.Ordinal));
            Assert.Single(first.Runs.Select(run => run.EventHash).Distinct(StringComparer.Ordinal));
            Assert.Equal(first.EvidenceHash, second.EvidenceHash);

            DeterministicMultiAxisCommissioningResultPackage.SaveToJson(first, resultPath);
            var restored = Assert.IsType<DeterministicMultiAxisCommissioningResultPackage>(
                DeterministicMultiAxisCommissioningResultPackage.LoadFromJson(resultPath));
            Assert.True(restored.HasValidEvidenceHash());
            Assert.True(restored.IsForContext(project.Id, projectJson, FixedStep, recipe));

            recipe.Targets[0].TargetPosition += 1;
            Assert.False(restored.IsForContext(project.Id, projectJson, FixedStep, recipe));
            Assert.False((restored with { EvidenceHash = new string('0', 64) }).HasValidEvidenceHash());
        }
        finally
        {
            File.Delete(resultPath);
        }
    }

    [Fact]
    public async Task PersistedRecipe_HistoryBaselineFindsFirstChangedAxisTickAndRoundTrips()
    {
        MachineProjectDocument project = LoadSample();
        MultiAxisCommissioningRecipeDefinition recipe = Assert.IsType<MultiAxisCommissioningRecipeDefinition>(
            project.MultiAxisCommissioningRecipe);
        string projectJson = new ProjectDocumentStore().SerializeForEvidence(project);
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project).Configuration);
        string projectPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ovmachine");
        string baselinePath = $"{projectPath}.commissioning-baseline.json";
        string historyPath = $"{projectPath}.commissioning-history.json";
        try
        {
            var runner = new DeterministicMultiAxisCommissioningRunner();
            DeterministicMultiAxisCommissioningResultPackage accepted = await runner.RunAsync(
                runtime,
                project.Id,
                project.Name,
                projectPath,
                projectJson,
                recipe,
                FixedStep);
            DeterministicMultiAxisCommissioningBaseline baseline =
                DeterministicMultiAxisCommissioningBaseline.FromResult(accepted);
            Assert.True(baseline.HasValidEvidenceHash());
            Assert.True(baseline.CompareTo(accepted).IsMatch);

            SimulationRuntimeConfiguration changedRuntime = WithAxisVelocity(
                runtime,
                recipe.Targets[0].AxisId,
                runtime.Axes.Single(axis => axis.Id == recipe.Targets[0].AxisId).MaximumVelocity / 2);
            DeterministicMultiAxisCommissioningResultPackage changed = await runner.RunAsync(
                changedRuntime,
                project.Id,
                project.Name,
                projectPath,
                projectJson,
                recipe,
                FixedStep);

            Assert.True(changed.IsSuccess);
            DeterministicCommissioningBaselineComparison comparison = baseline.CompareTo(changed);
            Assert.False(comparison.IsMatch);
            Assert.Equal("SnapshotEvidenceMismatch", comparison.MismatchCode);
            DeterministicCommissioningMismatch mismatch = Assert.IsType<DeterministicCommissioningMismatch>(
                comparison.FirstMismatch);
            Assert.Equal(recipe.Targets[0].AxisId, mismatch.TargetId);
            Assert.Equal(0, mismatch.TickIndex);

            recipe.Targets[0].TargetPosition += 1;
            DeterministicMultiAxisCommissioningResultPackage changedTarget = await runner.RunAsync(
                runtime,
                project.Id,
                project.Name,
                projectPath,
                new ProjectDocumentStore().SerializeForEvidence(project),
                recipe,
                FixedStep);
            DeterministicCommissioningMismatch targetMismatch =
                Assert.IsType<DeterministicCommissioningMismatch>(
                    baseline.CompareTo(changedTarget).FirstMismatch);
            Assert.Equal("Event", targetMismatch.EvidenceKind);
            Assert.Equal(recipe.Targets[0].AxisId, targetMismatch.TargetId);
            Assert.Equal(0, targetMismatch.TickIndex);

            recipe.Targets[0].TargetPosition -= 1;
            recipe.Targets[1].TargetPosition += 1;
            DeterministicMultiAxisCommissioningResultPackage changedSecondTarget = await runner.RunAsync(
                runtime,
                project.Id,
                project.Name,
                projectPath,
                new ProjectDocumentStore().SerializeForEvidence(project),
                recipe,
                FixedStep);
            DeterministicCommissioningMismatch secondTargetMismatch =
                Assert.IsType<DeterministicCommissioningMismatch>(
                    baseline.CompareTo(changedSecondTarget).FirstMismatch);
            Assert.Equal("Event", secondTargetMismatch.EvidenceKind);
            Assert.Equal(recipe.Targets[1].AxisId, secondTargetMismatch.TargetId);
            Assert.Equal(0, secondTargetMismatch.TickIndex);

            DeterministicMultiAxisCommissioningResultHistory history =
                DeterministicMultiAxisCommissioningResultHistory.Empty(project.Id)
                    .Append(accepted, DateTimeOffset.Parse("2026-08-12T00:00:00Z"))
                    .Append(changed, DateTimeOffset.Parse("2026-08-12T00:01:00Z"));
            for (var index = 0; index < DeterministicMultiAxisCommissioningResultHistory.MaximumEntries; index++)
            {
                history = history.Append(accepted, DateTimeOffset.Parse("2026-08-12T00:02:00Z").AddMinutes(index));
            }
            Assert.True(history.HasValidEvidenceHash());
            Assert.Equal(DeterministicMultiAxisCommissioningResultHistory.MaximumEntries, history.Entries.Length);
            Assert.Equal(3, history.Entries[0].Sequence);

            DeterministicMultiAxisCommissioningBaseline.SaveToJson(baseline, baselinePath);
            DeterministicMultiAxisCommissioningResultHistory.SaveToJson(history, historyPath);
            Assert.True(DeterministicMultiAxisCommissioningBaseline.LoadFromJson(baselinePath)?.HasValidEvidenceHash());
            Assert.True(DeterministicMultiAxisCommissioningResultHistory.LoadFromJson(historyPath)?.HasValidEvidenceHash());
        }
        finally
        {
            File.Delete(baselinePath);
            File.Delete(historyPath);
        }
    }

    [Fact]
    public async Task PersistedRecipe_ProducesIdenticalStateAndOrderedEvidenceAcrossRuns()
    {
        MachineProjectDocument project = LoadSample();
        MultiAxisCommissioningRecipeDefinition recipe = Assert.IsType<MultiAxisCommissioningRecipeDefinition>(
            project.MultiAxisCommissioningRecipe);
        Assert.Equal(new[] { "y", "x" }, recipe.Targets.Select(target => target.AxisId));

        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);

        var runs = new List<string[]>();
        for (var run = 0; run < 2; run++)
        {
            using var engine = new FixedStepSimulationEngine(new SimulationSettings
            {
                FixedStep = FixedStep,
                Seed = project.Simulation.Seed
            });
            await engine.StartAsync();
            Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(runtime))).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
            Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
            SimulationSnapshot paused = engine.CurrentSnapshot;

            var move = new MoveAxesAbsoluteCommand(recipe.Targets.Select(target =>
                new AxisMoveTarget(target.AxisId, target.TargetPosition)));
            SimulationCommandResult moveResult = await engine.EnqueueCommandAsync(move);
            Assert.True(moveResult.IsAccepted, moveResult.Detail);
            Assert.All(engine.CurrentSnapshot.Axes, axis => Assert.Equal(AxisState.Moving, axis.State));

            Assert.True((await engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            SimulationSnapshot stepped = await WaitForSnapshotAsync(
                engine.SnapshotReader,
                snapshot => snapshot.TickIndex > paused.TickIndex);
            Assert.Equal(paused.TickIndex + 1, stepped.TickIndex);
            Assert.All(stepped.Axes, axis => Assert.True(axis.Position > 0));

            var stop = new StopAxesCommand(recipe.Targets.Select(target => target.AxisId));
            SimulationCommandResult stopResult = await engine.EnqueueCommandAsync(stop);
            Assert.True(stopResult.IsAccepted, stopResult.Detail);
            Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
            Assert.Equal(0, engine.CurrentSnapshot.TickIndex);
            Assert.All(engine.CurrentSnapshot.Axes, axis =>
            {
                Assert.Equal(AxisState.Idle, axis.State);
                Assert.Equal(0, axis.Position, 10);
            });

            await engine.StopAsync();
            IReadOnlyList<SimulationEvent> events = await ReadAllEventsAsync(engine);
            SimulationEvent moveEvent = Assert.Single(events, item =>
                item.Code == "AxisGroupMoveAccepted" && item.CommandId == move.CommandId);
            SimulationEvent stopEvent = Assert.Single(events, item =>
                item.Code == "AxisGroupStopAccepted" && item.CommandId == stop.CommandId);
            Assert.Contains("Targets: y = 120.000, x = 240.000.", moveEvent.Message, StringComparison.Ordinal);
            Assert.Contains("Stopped: y = ", stopEvent.Message, StringComparison.Ordinal);

            runs.Add(new[]
            {
                string.Join("|", stepped.Axes.Select(axis => $"{axis.Id}:{axis.Position:F10}:{axis.State}")),
                $"{moveEvent.TickIndex - paused.TickIndex}|{moveEvent.Code}|{moveEvent.Message}",
                $"{stopEvent.TickIndex - paused.TickIndex}|{stopEvent.Code}|{stopEvent.Message}"
            });
        }

        Assert.Equal(runs[0], runs[1]);
    }

    [Fact]
    public async Task PersistedRecipe_InvalidTargetIsRejectedAtomically()
    {
        MachineProjectDocument project = LoadSample();
        MultiAxisCommissioningRecipeDefinition recipe = Assert.IsType<MultiAxisCommissioningRecipeDefinition>(
            project.MultiAxisCommissioningRecipe);
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        SimulationRuntimeConfiguration runtime = Assert.IsType<SimulationRuntimeConfiguration>(
            compilation.Configuration);

        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = FixedStep });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(runtime))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        AxisMoveTarget[] targets = recipe.Targets.Select((target, index) =>
            new AxisMoveTarget(target.AxisId, index == 0 ? 10_000 : target.TargetPosition)).ToArray();
        SimulationCommandResult result = await engine.EnqueueCommandAsync(
            new MoveAxesAbsoluteCommand(targets));

        Assert.False(result.IsAccepted);
        Assert.Equal(SimulationCommandErrorCode.AxisTargetOutOfRange, result.ErrorCode);
        Assert.All(engine.CurrentSnapshot.Axes, axis =>
        {
            Assert.Equal(AxisState.Idle, axis.State);
            Assert.Equal(0, axis.Position, 10);
        });
        await engine.StopAsync();
    }

    private static MachineProjectDocument LoadSample()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sample-pick-and-place.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static SimulationRuntimeConfiguration WithAxisVelocity(
        SimulationRuntimeConfiguration runtime,
        string axisId,
        double maximumVelocity) =>
        new(
            runtime.Axes.Select(axis => new AxisConfiguration
            {
                Id = axis.Id,
                Name = axis.Name,
                MinimumPosition = axis.MinimumPosition,
                MaximumPosition = axis.MaximumPosition,
                HomePosition = axis.HomePosition,
                MaximumVelocity = axis.Id == axisId ? maximumVelocity : axis.MaximumVelocity,
                Acceleration = axis.Acceleration,
                Deceleration = axis.Deceleration,
                FollowingErrorLimit = axis.FollowingErrorLimit
            }),
            runtime.Channels,
            runtime.Sequences,
            runtime.Cameras,
            runtime.AutomaticRun,
            runtime.Layout);

    private static async Task<SimulationSnapshot> WaitForSnapshotAsync(
        ChannelReader<SimulationSnapshot> reader,
        Func<SimulationSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (SimulationSnapshot snapshot in reader.ReadAllAsync(timeout.Token))
        {
            if (predicate(snapshot))
            {
                return snapshot;
            }
        }

        throw new TimeoutException("Expected simulation snapshot was not published.");
    }

    private static async Task<IReadOnlyList<SimulationEvent>> ReadAllEventsAsync(
        FixedStepSimulationEngine engine)
    {
        var events = new List<SimulationEvent>();
        await foreach (SimulationEvent item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }

        return events;
    }

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(Environment.NewLine, compilation.Errors.Select(error =>
            $"{error.Code} [{error.TargetId}]: {error.Message}"));
}
