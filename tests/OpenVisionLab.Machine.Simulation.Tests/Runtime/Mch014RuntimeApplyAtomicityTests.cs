using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class Mch014RuntimeApplyAtomicityTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void InvalidLateLayoutCandidateLeavesEveryExistingRuntimeDomainUntouched()
    {
        var state = new SimulationRuntimeState(FixedStep, 1.0);
        SimulationRuntimeConfiguration existing = LoadVisionRuntime();
        Assert.True(
            state.TryApplyRuntimeConfiguration(existing, "existing-project", out var configureError),
            configureError);

        state.RunMode = SimulationRunMode.RealTime;
        state.PendingSteps = 2;
        state.ActiveSequenceId = existing.Sequences[0].Id;
        state.AdvanceTick();

        var beforeSnapshot = state.CreateSnapshot();
        var beforeAxis = Assert.Single(state.Axes);
        var beforeCamera = Assert.Single(state.Cameras);
        var beforeSignalHub = state.SignalHub;
        var beforeLayout = state.MachineLayout;
        var beforeGeneration = state.RuntimeGeneration;

        var invalidLayout = new MachineLayoutRuntimeConfiguration(
            "replacement-layout",
            "Replacement Layout",
            new LayoutComponentRuntimeConfiguration[]
            {
                new MachineFrameRuntimeConfiguration(
                    "replacement-frame",
                    "Replacement Frame",
                    new LayoutRuntimeTransform(0, 0),
                    new LayoutRuntimeSize(10, 10)),
                new LinearStageRuntimeConfiguration(
                    "replacement-stage",
                    "Replacement Stage",
                    "missing-axis",
                    0,
                    new LayoutRuntimeTransform(0, 0),
                    new LayoutRuntimeSize(2, 2))
            });
        var invalid = new SimulationRuntimeConfiguration(
            existing.Axes,
            existing.Channels,
            existing.Sequences,
            existing.Cameras,
            existing.AutomaticRun,
            invalidLayout,
            existing.PickPlaceWorkpiece,
            existing.TimeScale);

        var accepted = state.TryApplyRuntimeConfiguration(
            invalid,
            "replacement-project",
            out var error);

        Assert.False(accepted);
        Assert.Contains("missing-axis", error, StringComparison.Ordinal);
        Assert.Equal(beforeGeneration, state.RuntimeGeneration);
        Assert.Equal("existing-project", state.ProjectId);
        Assert.Same(beforeAxis, Assert.Single(state.Axes));
        Assert.Same(beforeCamera, Assert.Single(state.Cameras));
        Assert.Same(beforeSignalHub, state.SignalHub);
        Assert.Same(beforeLayout, state.MachineLayout);
        Assert.Equal(beforeSnapshot.TickIndex, state.TickIndex);
        Assert.Equal(beforeSnapshot.SimulationTime, state.SimulationTime);
        Assert.Equal(beforeSnapshot.RunMode, state.RunMode);
        Assert.Equal(beforeSnapshot.ProjectId, state.CreateSnapshot().ProjectId);
        Assert.Equal(beforeSnapshot.RuntimeGeneration, state.CreateSnapshot().RuntimeGeneration);
        Assert.Equal(
            beforeSnapshot.Axes.Select(axis => axis.Id),
            state.CreateSnapshot().Axes.Select(axis => axis.Id));
        Assert.Equal(
            beforeSnapshot.Cameras.Select(camera => camera.Id),
            state.CreateSnapshot().Cameras.Select(camera => camera.Id));
        Assert.Equal(
            beforeSnapshot.Sequences.Select(sequence => sequence.SequenceId),
            state.CreateSnapshot().Sequences.Select(sequence => sequence.SequenceId));
        Assert.Equal(
            beforeSnapshot.LayoutComponents.Select(component => component.Id),
            state.CreateSnapshot().LayoutComponents.Select(component => component.Id));
    }

    [Fact]
    public void ValidCandidateReplacesAllRuntimeDomainsAndIdentityTogether()
    {
        var state = new SimulationRuntimeState(FixedStep, 1.0);
        SimulationRuntimeConfiguration existing = LoadVisionRuntime();
        Assert.True(
            state.TryApplyRuntimeConfiguration(existing, "existing-project", out var existingError),
            existingError);
        state.AdvanceTick();
        var beforeGeneration = state.RuntimeGeneration;
        var beforeAxis = Assert.Single(state.Axes);
        var beforeCamera = Assert.Single(state.Cameras);
        var beforeLayout = state.MachineLayout;

        var accepted = state.TryApplyRuntimeConfiguration(
            existing,
            "replacement-project",
            out var replacementError);

        Assert.True(accepted, replacementError);
        Assert.Equal(beforeGeneration + 1, state.RuntimeGeneration);
        Assert.Equal("replacement-project", state.ProjectId);
        Assert.Equal(0, state.TickIndex);
        Assert.Equal(SimulationRunMode.Paused, state.RunMode);
        Assert.NotSame(beforeAxis, Assert.Single(state.Axes));
        Assert.NotSame(beforeCamera, Assert.Single(state.Cameras));
        Assert.NotSame(beforeLayout, state.MachineLayout);
        Assert.Equal(
            existing.Axes.Select(axis => axis.Id),
            state.CreateSnapshot().Axes.Select(axis => axis.Id));
        Assert.Equal(
            existing.Cameras.Select(camera => camera.Id),
            state.CreateSnapshot().Cameras.Select(camera => camera.Id));
        Assert.Equal(
            existing.Sequences.Select(sequence => sequence.Id),
            state.CreateSnapshot().Sequences.Select(sequence => sequence.SequenceId));
        Assert.Equal(
            existing.Layout!.Components.Select(component => component.Id),
            state.CreateSnapshot().LayoutComponents.Select(component => component.Id));
    }

    private static SimulationRuntimeConfiguration LoadVisionRuntime()
    {
        var projectPath = Path.Combine(AppContext.BaseDirectory, "WaferEFEMVisionCell.ovmachine");
        var project = new ProjectDocumentStore().Load(File.ReadAllText(projectPath));
        var compilation = new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.True(
            compilation.IsSuccess,
            string.Join(Environment.NewLine, compilation.Errors.Select(error => error.Message)));
        Assert.NotNull(compilation.Configuration);
        Assert.NotEmpty(compilation.Configuration!.Cameras);
        Assert.NotNull(compilation.Configuration.Layout);
        return compilation.Configuration;
    }
}
