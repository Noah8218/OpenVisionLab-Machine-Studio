using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Workpieces;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class UnitMaterialFlowRuntimeTests
{
    [Fact]
    public void TotalSupplyIsSharedAndReceiversWaitForTheLastExitBeforeFinishing()
    {
        var configuration = Configuration() with { TotalFeedCount = 2, InspectionCameraIds = new Dictionary<string, IReadOnlyList<string>>() };
        var runtime = new UnitMaterialFlowRuntime(configuration);
        runtime.Reset("first");
        Assert.True(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        Assert.True(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "b"), TimeSpan.Zero).IsComplete);
        Assert.Equal(2, runtime.Supply.FedCount);
        Assert.True(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        Assert.True(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero).EndSequence);
        Assert.False(runtime.Execute(Op(SequenceStepAction.ReceiveUnitWorkpiece, "a"), TimeSpan.Zero).EndSequence);
        Assert.True(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "b"), TimeSpan.Zero).IsComplete);
        Assert.True(runtime.Execute(Op(SequenceStepAction.ReceiveUnitWorkpiece, "a"), TimeSpan.Zero).EndSequence);
        Assert.Equal(2, runtime.Supply.ExitedCount);
        Assert.False(runtime.Supply.HasInFlight);
        runtime.Reset("second");
        Assert.Equal(0, runtime.Supply.FedCount);
        Assert.True(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        Assert.StartsWith("second/", runtime.InstanceId("a"));
    }

    [Fact]
    public void ExplicitExitPositionUsesTheConfiguredNameAndVerdict()
    {
        var runtime = new UnitMaterialFlowRuntime(Configuration() with
        {
            Positions = [new("a", true, "cv.a", "NG", "불량 보관함")]
        });
        runtime.Reset("exit");
        runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero);
        runtime.RecordInspection("a", "camera", runtime.InstanceId("a"), SequenceVisionResultState.Failed);
        Assert.True(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        Assert.Equal("불량 보관함", Assert.Single(runtime.Exits).Route);
    }

    [Fact]
    public void SlotsReserveOneDestinationAndCarryTheSameInspectedInstance()
    {
        var runtime = CreateRuntime();
        Assert.All(runtime.CaptureSlots(), slot => Assert.Equal("Empty", slot.State));
        Assert.True(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        string? instance = runtime.InstanceId("a");
        Assert.False(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        Assert.True(runtime.BeginCapture("a", "camera", out var captured));
        Assert.Equal(instance, captured);
        runtime.RecordInspection("a", "camera", instance, SequenceVisionResultState.Passed);
        var transfer = Op(SequenceStepAction.TransferUnitWorkpiece, "a", "b", "cv.a", 1);
        Assert.False(runtime.Execute(transfer, TimeSpan.Zero).IsComplete);
        Assert.Equal("Reserved", runtime.CaptureSlots().Single(slot => slot.UnitId == "b").State);
        Assert.False(runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "b"), TimeSpan.Zero).IsComplete);
        Assert.False(runtime.Execute(transfer, TimeSpan.FromSeconds(.99)).IsComplete);
        Assert.True(runtime.Execute(transfer, TimeSpan.FromSeconds(1)).IsComplete);
        Assert.Null(runtime.InstanceId("a"));
        Assert.Equal(instance, runtime.InstanceId("b"));
        Assert.True(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "b"), TimeSpan.Zero).IsComplete);
        var exit = Assert.Single(runtime.Exits);
        Assert.Equal(instance, exit.InstanceId);
        Assert.Equal("OK", exit.Verdict);
        Assert.Equal("straight", exit.Route);
    }

    [Fact]
    public void OccupiedDestinationWaitsAndCancellationKeepsTheSourcePiece()
    {
        var runtime = CreateRuntime();
        runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero);
        runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "b"), TimeSpan.Zero);
        var transfer = Op(SequenceStepAction.TransferUnitWorkpiece, "a", "b", "cv.a", 1);
        Assert.False(runtime.Execute(transfer, TimeSpan.FromSeconds(10)).IsComplete);
        Assert.All(runtime.CaptureSlots(), slot => Assert.Equal("Occupied", slot.State));
        runtime.Reset("second");
        runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero);
        string? instance = runtime.InstanceId("a");
        runtime.Execute(transfer, TimeSpan.Zero);
        runtime.CancelTransfers();
        Assert.Equal(instance, runtime.InstanceId("a"));
        Assert.Equal("Empty", runtime.CaptureSlots().Single(slot => slot.UnitId == "b").State);
        Assert.False(runtime.Execute(transfer, TimeSpan.FromSeconds(10)).IsComplete);
        Assert.True(runtime.Execute(transfer, TimeSpan.FromSeconds(11)).IsComplete);
    }

    [Fact]
    public void StaleResultsCannotEjectANewPieceAndNgUsesItsConfiguredRoute()
    {
        var runtime = CreateRuntime();
        runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero);
        string? first = runtime.InstanceId("a");
        runtime.Reset("new");
        runtime.Execute(Op(SequenceStepAction.FeedUnitWorkpiece, "a"), TimeSpan.Zero);
        runtime.RecordInspection("a", "camera", first, SequenceVisionResultState.Passed);
        Assert.False(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "a"), TimeSpan.Zero).IsSuccess);
        runtime.RecordInspection("a", "camera", runtime.InstanceId("a"), SequenceVisionResultState.Failed);
        Assert.False(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "a") with { Route = "OK" }, TimeSpan.Zero).IsSuccess);
        Assert.True(runtime.Execute(Op(SequenceStepAction.EjectUnitWorkpiece, "a"), TimeSpan.Zero).IsComplete);
        Assert.Equal("side", Assert.Single(runtime.Exits).Route);
        runtime.Reset("recovery");
        Assert.Empty(runtime.Exits);
        Assert.All(runtime.CaptureSlots(), slot => Assert.Null(slot.InstanceId));
    }

    internal static UnitMaterialFlowRuntime CreateRuntime() => new(Configuration());
    internal static UnitMaterialFlowConfiguration Configuration() => new(true, "part", "straight", "side", ["a", "b"],
        new Dictionary<string, IReadOnlyList<string>> { ["a"] = new[] { "camera" }, ["b"] = Array.Empty<string>() },
        new Dictionary<string, string> { ["cv.a"] = "a" });
    internal static UnitOperationStep Op(SequenceStepAction action, string unit, string? destination = null, string? conveyor = null, double seconds = 0) =>
        new(action.ToString(), action.ToString(), action, unit, destination, conveyor, "auto", TimeSpan.FromSeconds(seconds), null, null, TimeSpan.FromSeconds(30));
}
