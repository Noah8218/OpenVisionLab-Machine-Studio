using System.Security.Cryptography;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class RunHistoryIntegrationTests
{
    [Theory]
    [InlineData(NumericalCameraInputKind.Image2D)]
    [InlineData(NumericalCameraInputKind.HeightMap3D)]
    public async Task FastRunsRecordEveryOriginalFrameAndRestartUsesANewIdWithoutRewritingSettings(NumericalCameraInputKind inputKind)
    {
        var project = new MachineProjectDocument
        {
            Id = "history", Name = "History", MaterialFlow = new() { Enabled = true, TotalFeedCount = 3 },
            Stations = [new() { Id = "s", Name = "Station", Units = [new() { Id = "u", Name = "Unit", RepeatCount = 7 }] }],
            Devices = [new() { Id = "camera", Name = "Camera", Kind = DeviceKind.Camera, Camera = new() { NumericalInput = new NumericalCameraSettings { InputKind = inputKind, RecipeName = "inspection", GenerationDelayMilliseconds = 0, ResponseDelayMilliseconds = 0 }, ExposureDelayMilliseconds = 5, TransferDelayMilliseconds = 5 } }],
            Sequences = [new() { Id = "cycle", Name = "Cycle", UnitId = "u", Steps =
            [
                new() { Id = "feed", Name = "Feed", Action = SequenceStepAction.FeedUnitWorkpiece, TargetId = "u", NextStepId = "capture" },
                new() { Id = "capture", Name = "Capture", Action = SequenceStepAction.TriggerCamera, TargetId = "camera", Parameter = "inspection", NextStepId = "inspect" },
                new() { Id = "inspect", Name = "Inspect", Action = SequenceStepAction.WaitVisionResult, TargetId = "camera", NextStepId = "exit", FailureStepId = "exit", TimeoutMs = 1000 },
                new() { Id = "exit", Name = "Exit", Action = SequenceStepAction.EjectUnitWorkpiece, TargetId = "u", Parameter = "auto", NextStepId = "done" },
                new() { Id = "done", Name = "Done", Action = SequenceStepAction.Complete }
            ] }]
        };
        var compiled = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project, buildIdentity: "test-current-build");
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Errors));
        using var engine = new FixedStepSimulationEngine(new() { FixedStep = TimeSpan.FromMilliseconds(5), EventBufferCapacity = 2 });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(compiled.Configuration!, project.Id))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("cycle"))).IsAccepted);
        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);
        Assert.Equal(1, engine.CurrentSnapshot.MaterialSupply!.FedCount);
        var firstActiveId = Assert.IsType<string>(engine.ActiveRunId);
        Assert.NotEmpty(firstActiveId);
        Assert.False(engine.RunRecordReader.TryPeek(out _));
        Assert.True((await engine.EnqueueCommandAsync(new PlayCommand())).IsAccepted);
        var first = await engine.RunRecordReader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first!.Outcome == "Completed", first.Outcome + ": " + string.Join("; ", first.Faults));
        Assert.Equal(firstActiveId, first.Id);
        Assert.Equal(3, first.Products.Count);
        Assert.Equal(3, first.Captures.Count);
        Assert.Equal(3, first.ActualFeedCount);
        Assert.Equal(7, new ProjectDocumentStore().Load(first.AppliedRecipeJson).Stations[0].Units[0].RepeatCount);
        Assert.All(first.Captures, capture =>
        {
            Assert.NotNull(capture.Pixels);
            Assert.Equal(capture.ContentSha256, Convert.ToHexString(SHA256.HashData(capture.Pixels!)));
            Assert.Contains(first.Products, product => product.Id == capture.ProductId);
            Assert.NotNull(capture.ResultJson);
        });
        var evidenceDirectory = Path.Combine(Path.GetTempPath(), "ActualRunHistoryEvidence");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, inputKind + ".ovmachine"), first.AppliedRecipeJson);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, inputKind + "-record.json"), JsonSerializer.Serialize(first));
        await engine.EnqueueCommandAsync(new ResetCommand());
        Assert.Null(engine.ActiveRunId);
        await engine.EnqueueCommandAsync(new StartSequenceCommand("cycle"));
        var secondActiveId = Assert.IsType<string>(engine.ActiveRunId);
        Assert.NotEqual(firstActiveId, secondActiveId);
        await engine.EnqueueCommandAsync(new PlayCommand());
        var second = await engine.RunRecordReader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Completed", second.Outcome);
        Assert.Equal(secondActiveId, second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(3, second.Captures.Count);
        Assert.Empty(first.Products.Select(product => product.Id).Intersect(second.Products.Select(product => product.Id)));
        await engine.EnqueueCommandAsync(new ResetCommand());
        await engine.EnqueueCommandAsync(new StartSequenceCommand("cycle"));
        await engine.EnqueueCommandAsync(new StepCommand());
        var stoppedActiveId = Assert.IsType<string>(engine.ActiveRunId);
        await engine.StopAsync();
        Assert.Null(engine.ActiveRunId);
        var stopped = await engine.RunRecordReader.ReadAsync();
        Assert.Equal(stoppedActiveId, stopped.Id);
        Assert.Equal("Stopped", stopped.Outcome);
        Assert.NotEqual(first.Id, stopped.Id);
        Assert.Equal(3, first.Captures.Count);
    }

    [Fact]
    public async Task APreviouslyCompletedSequenceDoesNotMarkTheNextAbortedRunAsCompleted()
    {
        var project = new MachineProjectDocument
        {
            Id = "history-status", Name = "History status",
            Sequences =
            [
                new() { Id = "finished", Name = "Finished", Steps = [new() { Id = "done", Name = "Done", Action = SequenceStepAction.Complete }] },
                new() { Id = "aborted", Name = "Aborted", Steps =
                    [new() { Id = "delay", Name = "Delay", Action = SequenceStepAction.Delay, Parameter = "1000", NextStepId = "done" },
                     new() { Id = "done", Name = "Done", Action = SequenceStepAction.Complete }] }
            ]
        };
        var compiled = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5)).Compile(project);
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Errors));
        using var engine = new FixedStepSimulationEngine(new() { FixedStep = TimeSpan.FromMilliseconds(5) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(compiled.Configuration!, project.Id))).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("finished"))).IsAccepted);
        await engine.EnqueueCommandAsync(new StepCommand());
        Assert.Equal("Completed", (await engine.RunRecordReader.ReadAsync()).Outcome);
        Assert.True((await engine.EnqueueCommandAsync(new StartSequenceCommand("aborted"))).IsAccepted);
        var abortedId = Assert.IsType<string>(engine.ActiveRunId);
        await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.Equal(abortedId, engine.ActiveRunId);
        Assert.False(engine.RunRecordReader.TryPeek(out _));
        Assert.True((await engine.EnqueueCommandAsync(new AbortSequenceCommand("aborted"))).IsAccepted);
        var aborted = await engine.RunRecordReader.ReadAsync();
        Assert.Equal("Stopped", aborted.Outcome);
        Assert.Equal(abortedId, aborted.Id);
        Assert.Null(engine.ActiveRunId);
        await engine.StopAsync();
    }
}
