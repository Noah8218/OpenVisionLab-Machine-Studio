using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ManualCameraTriggerWorkflowTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task AcceptedTriggerDispatchesCommandAndRefreshesMonitor()
    {
        using var fixture = new Fixture();
        var monitorSnapshots = new List<SimulationSnapshot>();
        SimulationCommand? dispatched = null;
        var workflow = fixture.CreateWorkflow(
            (command, _) =>
            {
                dispatched = command;
                return Task.FromResult(Accepted(command));
            },
            monitorSnapshots.Add);

        var result = await workflow.ExecuteAsync(fixture.Request);

        Assert.Equal(ManualCameraTriggerOutcome.Accepted, result.Outcome);
        var trigger = Assert.IsType<TriggerVirtualCameraCommand>(dispatched);
        Assert.Equal("camera-top", trigger.CameraId);
        Assert.Equal("presence-check", trigger.RecipeId);
        Assert.Equal(fixture.Request.ProjectId, trigger.ProjectId);
        Assert.Equal(fixture.Request.RuntimeGeneration, trigger.RuntimeGeneration);
        Assert.Equal(PlaceholderInspectionDecision.Pass, trigger.InspectionEvidence?.Decision);
        Assert.Single(monitorSnapshots);
        Assert.True(fixture.Evidence.IsCapturing);
        fixture.Evidence.CancelCapture();
    }

    [Fact]
    public async Task MissingSourceReturnsSourceFailureWithoutDispatch()
    {
        using var fixture = new Fixture(writeSource: false);
        var dispatchCount = 0;
        var workflow = fixture.CreateWorkflow(
            (_, _) =>
            {
                dispatchCount++;
                return Task.FromResult(Accepted(new PauseCommand()));
            },
            _ => { });

        var result = await workflow.ExecuteAsync(fixture.Request);

        Assert.Equal(ManualCameraTriggerOutcome.SourceRejected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Equal(0, dispatchCount);
        Assert.False(fixture.Evidence.IsCapturing);
    }

    [Fact]
    public async Task ChangedRuntimeContextRejectsBeforeEvidenceOrDispatch()
    {
        using var fixture = new Fixture(writeSource: true);
        var changedSnapshot = fixture.CreateSnapshot(tickIndex: fixture.Request.BaselineSnapshot.TickIndex + 1);
        var dispatchCount = 0;
        var workflow = fixture.CreateWorkflow(
            (_, _) =>
            {
                dispatchCount++;
                return Task.FromResult(Accepted(new PauseCommand()));
            },
            _ => { },
            () => changedSnapshot);

        var result = await workflow.ExecuteAsync(fixture.Request);

        Assert.Equal(ManualCameraTriggerOutcome.ContextChanged, result.Outcome);
        Assert.Equal(0, dispatchCount);
        Assert.False(fixture.Evidence.IsCapturing);
    }

    [Fact]
    public async Task ChangedProjectRuntimeIdentityRejectsBeforeEvidenceOrDispatch()
    {
        using var fixture = new Fixture(writeSource: true);
        var changedSnapshot = fixture.CreateSnapshot(
            projectId: "other-project",
            runtimeGeneration: fixture.Request.RuntimeGeneration + 1);
        var dispatchCount = 0;
        var workflow = fixture.CreateWorkflow(
            (_, _) =>
            {
                dispatchCount++;
                return Task.FromResult(Accepted(new PauseCommand()));
            },
            _ => { },
            () => changedSnapshot);

        var result = await workflow.ExecuteAsync(fixture.Request);

        Assert.Equal(ManualCameraTriggerOutcome.ContextChanged, result.Outcome);
        Assert.Equal(0, dispatchCount);
        Assert.False(fixture.Evidence.IsCapturing);
    }

    [Fact]
    public async Task ChangedDocumentRevisionRejectsBeforeEvidenceOrDispatch()
    {
        using var fixture = new Fixture(writeSource: true);
        var dispatchCount = 0;
        var workflow = fixture.CreateWorkflow(
            (_, _) =>
            {
                dispatchCount++;
                return Task.FromResult(Accepted(new PauseCommand()));
            },
            _ => { },
            getCurrentSessionIdentity: () => new(
                fixture.Request.SessionId,
                fixture.Request.DocumentRevision + 1));

        var result = await workflow.ExecuteAsync(fixture.Request);

        Assert.Equal(ManualCameraTriggerOutcome.ContextChanged, result.Outcome);
        Assert.Equal(0, dispatchCount);
        Assert.False(fixture.Evidence.IsCapturing);
    }

    [Fact]
    public async Task CancellationDuringDispatchCancelsPendingEvidence()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var dispatchStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = fixture.CreateWorkflow(
            async (_, _) =>
            {
                dispatchStarted.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
                return Accepted(new PauseCommand());
            },
            _ => { });

        var execution = workflow.ExecuteAsync(fixture.Request, cancellation.Token);
        await dispatchStarted.Task;
        Assert.True(fixture.Evidence.IsCapturing);
        cancellation.Cancel();

        var exception = await Record.ExceptionAsync(() => execution);
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(fixture.Evidence.IsCapturing);
    }

    [Fact]
    public async Task RejectedDispatchCancelsPendingEvidence()
    {
        using var fixture = new Fixture();
        var workflow = fixture.CreateWorkflow(
            (command, _) => Task.FromResult(Rejected(command)),
            _ => { });

        var result = await workflow.ExecuteAsync(fixture.Request);

        Assert.Equal(ManualCameraTriggerOutcome.DispatchRejected, result.Outcome);
        Assert.False(fixture.Evidence.IsCapturing);
    }

    private static SimulationCommandResult Accepted(SimulationCommand command) =>
        new(
            command.CommandId,
            true,
            20,
            TimeSpan.FromMilliseconds(100),
            SimulationCommandErrorCode.None,
            null);

    private static SimulationCommandResult Rejected(SimulationCommand command) =>
        new(
            command.CommandId,
            false,
            20,
            TimeSpan.FromMilliseconds(100),
            SimulationCommandErrorCode.CameraTriggerRejected,
            "rejected");

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly bool _writeSource;

        public Fixture(bool writeSource = true)
        {
            _writeSource = writeSource;
            _root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio\\manual-camera-trigger-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            if (_writeSource)
            {
                File.WriteAllBytes(Path.Combine(_root, "input.raw"), [0x10, 0x20, 0x30, 0x40]);
            }

            Request = CreateRequest(CreateSnapshot());
            Evidence = new VisionExecutionEvidenceViewModel(
                () => new VisionEvidenceContext(
                    Request.ProjectId,
                    Request.ProjectJson,
                    Request.BuildIdentity,
                    Request.ProjectPath,
                    Request.InspectionRequest.CameraId,
                    Request.InspectionRequest.RecipeId),
                _ => { },
                _ => { });
        }

        public ManualCameraTriggerRequest Request { get; }

        public VisionExecutionEvidenceViewModel Evidence { get; }

        public ManualCameraTriggerWorkflow CreateWorkflow(
            Func<SimulationCommand, string, Task<SimulationCommandResult>> dispatch,
            Action<SimulationSnapshot> applyMonitorSnapshot,
            Func<SimulationSnapshot>? getCurrentSnapshot = null,
            Func<ManualCameraSessionIdentity>? getCurrentSessionIdentity = null) =>
            new(
                getCurrentSnapshot ?? (() => Request.BaselineSnapshot),
                dispatch,
                Evidence,
                applyMonitorSnapshot,
                getCurrentSessionIdentity ?? (() => new(
                    Request.SessionId,
                    Request.DocumentRevision)));

        public SimulationSnapshot CreateSnapshot(
            long tickIndex = 20,
            string projectId = "manual-camera-project",
            long runtimeGeneration = 1) =>
            new(
                TimeSpan.FromMilliseconds(100),
                tickIndex,
                SimulationRunMode.Paused,
                SimulationControlOwner.Manual,
                1,
                [],
                0,
                [],
                [],
                [new VirtualCameraSnapshot(
                    "camera-top",
                    "Top camera",
                    VirtualCameraState.Idle,
                    3,
                    null,
                    null,
                    0,
                    0,
                    null)],
                AutomaticRunSnapshot.NotConfigured,
                Array.Empty<LayoutComponentSnapshot>(),
                projectId: projectId,
                runtimeGeneration: runtimeGeneration);

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private ManualCameraTriggerRequest CreateRequest(SimulationSnapshot snapshot) =>
            new(
                "manual-camera-session",
                4,
                "manual-camera-project",
                "Manual Camera Project",
                Path.Combine(_root, "machine.ovmachine"),
                "{\"id\":\"manual-camera-project\"}",
                "0.1.0-test+manual-camera",
                FixedStep,
                snapshot,
                snapshot.Cameras.Single(),
                new VirtualCameraInspectionRequest(
                    Path.Combine(_root, "machine.ovmachine"),
                    "camera-top",
                    "presence-check",
                    3,
                    PlaceholderInspectionDecision.Pass,
                    "input.raw",
                    2,
                    2,
                    "Mono8",
                    snapshot.TickIndex,
                    snapshot.SimulationTime,
                    1234,
                    new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["axis-x"] = 10.5
                    }),
                snapshot.RuntimeGeneration);
    }
}
