using System.Threading.Channels;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectRuntimeApplicationWorkflowTests
{
    [Fact]
    public async Task InvalidSequenceOpenShowsReasonPreservesProjectAndAllowsValidReopen()
    {
        string directory = Path.Combine(@"D:\OpenVisionLab-TestData\Machine\project-runtime-application-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string rejectedPath = Path.Combine(directory, "invalid-sequence.ovmachine");
            string validPath = Path.Combine(directory, "valid.ovmachine");
            var invalid = new MachineProjectDocument
            {
                Name = "Invalid sequence",
                Sequences = [new()
                {
                    Id = "invalid", Name = "Invalid",
                    Steps = [new() { Id = "wait", Action = SequenceStepAction.WaitVisionResult, TargetId = "missing-camera", TimeoutMs = 3000, NextStepId = "done" },
                        new() { Id = "done", Action = SequenceStepAction.Complete }]
                }]
            };
            var store = new ProjectDocumentFileStore();
            await store.SaveAsync(invalid, rejectedPath);
            await store.SaveAsync(new MachineProjectDocument { Name = "Recovered" }, validPath);
            using var viewModel = new MainViewModel(new MachineProjectDocument { Name = "Retained" });
            var retained = Assert.Single(viewModel.ProjectTree.Roots);
            var details = new List<string>();
            viewModel.ProjectOpenFailurePresenter = details.Add;

            Assert.False(await viewModel.OpenProjectAsync(rejectedPath));
            Assert.Same(retained, Assert.Single(viewModel.ProjectTree.Roots));
            Assert.Null(viewModel.CurrentProjectPath);
            Assert.Contains("FailureStepRequired", Assert.Single(details));
            Assert.Equal(OpenVisionLanguageService.T("Project.OpenFailedStatus"), viewModel.StatusMessage);

            Assert.True(await viewModel.OpenProjectAsync(validPath));
            Assert.Equal("Recovered", Assert.Single(viewModel.ProjectTree.Roots).DisplayName);
            Assert.Equal(Path.GetFullPath(validPath), viewModel.CurrentProjectPath);
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.HasUnsavedChanges);
            Assert.Single(details);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AppliesProjectAndRestoresApplyingStateAfterCompletion()
    {
        using var engine = new TestSimulationEngine();
        var definitionWorkflow = new RuntimeDefinitionApplicationWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5));
        var applyingStates = new List<bool>();
        MachineProjectDocument? appliedProject = null;
        RuntimeDefinitionApplicationResult? rejectedResult = null;
        var workflow = new ProjectRuntimeApplicationWorkflow(
            definitionWorkflow,
            applyingStates.Add,
            result => rejectedResult = result,
            project => appliedProject = project);
        var project = new MachineProjectDocument { Name = "Applied project" };

        var accepted = await workflow.ApplyAsync(project);

        Assert.True(accepted);
        Assert.False(workflow.IsApplying);
        Assert.Equal([true, false], applyingStates);
        Assert.Same(project, appliedProject);
        Assert.Null(rejectedResult);
        Assert.Single(engine.Commands);
    }

    [Fact]
    public async Task RejectsConcurrentProjectApplicationWithoutEnqueuingAnotherCommand()
    {
        using var engine = new TestSimulationEngine(blockConfiguration: true);
        var definitionWorkflow = new RuntimeDefinitionApplicationWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5));
        var appliedProjects = new List<MachineProjectDocument>();
        var workflow = new ProjectRuntimeApplicationWorkflow(
            definitionWorkflow,
            _ => { },
            _ => { },
            appliedProjects.Add);
        var firstTask = workflow.ApplyAsync(new MachineProjectDocument { Name = "First" });

        await engine.ConfigurationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondAccepted = await workflow.ApplyAsync(
            new MachineProjectDocument { Name = "Second" });

        Assert.False(secondAccepted);
        Assert.Single(engine.Commands);

        engine.ReleaseConfiguration();
        Assert.True(await firstTask);
        Assert.Single(appliedProjects);
        Assert.Equal("First", appliedProjects[0].Name);
    }

    [Fact]
    public async Task ReportsEngineRejectionAndStillRestoresApplyingState()
    {
        using var engine = new TestSimulationEngine(acceptCommands: false);
        var definitionWorkflow = new RuntimeDefinitionApplicationWorkflow(
            engine,
            TimeSpan.FromMilliseconds(5));
        var applyingStates = new List<bool>();
        RuntimeDefinitionApplicationResult? rejectedResult = null;
        var workflow = new ProjectRuntimeApplicationWorkflow(
            definitionWorkflow,
            applyingStates.Add,
            result => rejectedResult = result,
            _ => Assert.Fail("A rejected project must not be committed."));

        var accepted = await workflow.ApplyAsync(new MachineProjectDocument { Name = "Rejected" });

        Assert.False(accepted);
        Assert.False(workflow.IsApplying);
        Assert.Equal([true, false], applyingStates);
        Assert.Equal(RuntimeDefinitionApplicationOutcome.EngineRejected, rejectedResult?.Outcome);
    }

    private sealed class TestSimulationEngine : ISimulationEngine
    {
        private readonly bool _acceptCommands;
        private readonly bool _blockConfiguration;
        private readonly TaskCompletionSource<bool> _configurationGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<SimulationSnapshot> _snapshotChannel = Channel.CreateUnbounded<SimulationSnapshot>();
        private readonly Channel<SimulationEvent> _eventChannel = Channel.CreateUnbounded<SimulationEvent>();

        internal TestSimulationEngine(bool acceptCommands = true, bool blockConfiguration = false)
        {
            _acceptCommands = acceptCommands;
            _blockConfiguration = blockConfiguration;
            ConfigurationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CurrentSnapshot = new SimulationSnapshot(
                TimeSpan.Zero,
                0,
                SimulationRunMode.Paused,
                SimulationControlOwner.Definition,
                1,
                [],
                0,
                [],
                []);
        }

        internal TaskCompletionSource<bool> ConfigurationStarted { get; }

        internal List<SimulationCommand> Commands { get; } = [];

        internal void ReleaseConfiguration() => _configurationGate.TrySetResult(true);

        public SimulationSnapshot CurrentSnapshot { get; }

        public ChannelReader<SimulationSnapshot> SnapshotReader => _snapshotChannel.Reader;

        public ChannelReader<SimulationEvent> EventReader => _eventChannel.Reader;

        public Task<SimulationEngineTerminationResult> Termination => Task.FromResult(
            new SimulationEngineTerminationResult(
                SimulationEngineTerminationOutcome.Normal,
                0,
                TimeSpan.Zero));

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<SimulationCommandResult> EnqueueCommandAsync(
            SimulationCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (_blockConfiguration && command is ConfigureRuntimeCommand)
            {
                ConfigurationStarted.TrySetResult(true);
                await _configurationGate.Task.WaitAsync(cancellationToken);
            }

            return new(
                command.CommandId,
                _acceptCommands,
                0,
                TimeSpan.Zero,
                _acceptCommands
                    ? SimulationCommandErrorCode.None
                    : SimulationCommandErrorCode.EngineFaulted,
                _acceptCommands ? null : "test rejection");
        }

        public void AddAxis(ServoAxisComponent axis)
        {
        }

        public void Dispose()
        {
            _snapshotChannel.Writer.TryComplete();
            _eventChannel.Writer.TryComplete();
        }
    }
}
