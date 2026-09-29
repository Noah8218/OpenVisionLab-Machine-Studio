using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class AxisCommissioningViewModelTests
{
    [Fact]
    public void ProjectionOwnsAxisPresentationAndInputValidation()
    {
        var dispatched = new List<(SimulationCommand Command, string Action)>();
        var viewModel = new AxisCommissioningViewModel(
            (command, action) =>
            {
                dispatched.Add((command, action));
                return Task.FromResult(
                    new SimulationCommandResult(
                        command.CommandId,
                        true,
                        1,
                        TimeSpan.FromMilliseconds(5),
                        SimulationCommandErrorCode.None,
                        null));
            },
            _ => { });
        var definition = new VirtualAxisDefinition
        {
            Id = "axis.x",
            Name = "X Axis",
            Unit = "mm",
            SoftLimitMin = 0,
            SoftLimitMax = 100,
            MaxVelocity = 50
        };

        viewModel.ApplyProjection(
            new AxisCommissioningProjection(
                new AxisSnapshot("axis.x", "X Axis", AxisState.Idle, 12.5, 0),
                definition,
                HasSelectedAxisStage: true,
                IsRunMode: true,
                IsApplyingProject: false,
                IsValidationBusy: false,
                RuntimeDefinitionDirty: false,
                IsRunning: true,
                ControlOwner: SimulationControlOwner.Manual,
                AutomaticRunActive: false,
                SequenceRunActive: false));

        Assert.True(viewModel.HasCurrentAxis);
        Assert.Equal("X Axis", viewModel.CurrentAxisName);
        Assert.Equal("12.500", viewModel.AxisTargetPositionText);
        Assert.True(viewModel.IsAxisTargetPositionValid);
        Assert.True(viewModel.CanMoveAxisAbsolute);

        viewModel.AxisTargetPositionText = "NaN";

        Assert.True(viewModel.HasAxisTargetPositionError);
        Assert.False(viewModel.IsAxisTargetPositionValid);
        Assert.False(viewModel.CanMoveAxisAbsolute);
        Assert.Empty(dispatched);
    }

    [Fact]
    public void ModeOnlyProjectionRefreshRaisesOnlyAxisCommandGates()
    {
        var viewModel = new AxisCommissioningViewModel(
            (command, _) => Task.FromResult(CreateAcceptedResult(command)),
            _ => { });
        var projection = new AxisCommissioningProjection(
            new AxisSnapshot("axis.x", "X Axis", AxisState.Idle, 12.5, 0),
            new VirtualAxisDefinition
            {
                Id = "axis.x",
                SoftLimitMin = 0,
                SoftLimitMax = 100,
                MaxVelocity = 50
            },
            HasSelectedAxisStage: true,
            IsRunMode: true,
            IsApplyingProject: false,
            IsValidationBusy: false,
            RuntimeDefinitionDirty: false,
            IsRunning: true,
            ControlOwner: SimulationControlOwner.Manual,
            AutomaticRunActive: false,
            SequenceRunActive: false);

        viewModel.ApplyProjection(projection, invalidateCommands: false);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ApplyProjection(projection with { IsRunMode = false }, invalidateCommands: false);

        Assert.Contains(nameof(AxisCommissioningViewModel.CanMoveAxisAbsolute), changedProperties);
        Assert.Contains(nameof(AxisCommissioningViewModel.CanJogAxis), changedProperties);
        Assert.DoesNotContain(nameof(AxisCommissioningViewModel.CurrentAxisName), changedProperties);
        Assert.DoesNotContain(nameof(AxisCommissioningViewModel.AxisTargetPositionValidationText), changedProperties);
    }

    [Fact]
    public void ProjectionGatesCommandsWithoutChangingRuntimeState()
    {
        var dispatchCount = 0;
        var viewModel = new AxisCommissioningViewModel(
            (command, _) =>
            {
                dispatchCount++;
                return Task.FromResult(
                    new SimulationCommandResult(
                        command.CommandId,
                        true,
                        1,
                        TimeSpan.Zero,
                        SimulationCommandErrorCode.None,
                        null));
            },
            _ => { });

        viewModel.ApplyProjection(
            new AxisCommissioningProjection(
                new AxisSnapshot("axis.x", "X Axis", AxisState.Idle, 12.5, 0),
                new VirtualAxisDefinition
                {
                    Id = "axis.x",
                    SoftLimitMin = 0,
                    SoftLimitMax = 100,
                    MaxVelocity = 50
                },
                HasSelectedAxisStage: true,
                IsRunMode: true,
                IsApplyingProject: false,
                IsValidationBusy: true,
                RuntimeDefinitionDirty: false,
                IsRunning: true,
                ControlOwner: SimulationControlOwner.Manual,
                AutomaticRunActive: false,
                SequenceRunActive: false));

        Assert.False(viewModel.CanMoveAxisAbsolute);
        Assert.False(viewModel.CanJogAxis);
        Assert.False(viewModel.HomeAxisCommand.CanExecute(null));
        Assert.Equal(0, dispatchCount);
    }

    [Fact]
    public async Task DisposeSuppressesLateJogStopAndBlocksFurtherCommands()
    {
        var startCompletion = new TaskCompletionSource<SimulationCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        var viewModel = new AxisCommissioningViewModel(
            (command, _) =>
            {
                dispatchCount++;
                return dispatchCount == 1
                    ? startCompletion.Task
                    : Task.FromResult(CreateAcceptedResult(command));
            },
            _ => { });

        viewModel.ApplyProjection(
            new AxisCommissioningProjection(
                new AxisSnapshot("axis.x", "X Axis", AxisState.Idle, 12.5, 0),
                new VirtualAxisDefinition
                {
                    Id = "axis.x",
                    SoftLimitMin = 0,
                    SoftLimitMax = 100,
                    MaxVelocity = 50
                },
                HasSelectedAxisStage: true,
                IsRunMode: true,
                IsApplyingProject: false,
                IsValidationBusy: false,
                RuntimeDefinitionDirty: false,
                IsRunning: true,
                ControlOwner: SimulationControlOwner.Manual,
                AutomaticRunActive: false,
                SequenceRunActive: false));

        Assert.True(viewModel.BeginAxisJog(AxisJogDirection.Positive));
        var endJogTask = viewModel.EndAxisJogAsync();

        viewModel.Dispose();
        viewModel.Dispose();
        viewModel.AxisTargetPositionText = "25";
        viewModel.ApplyProjection(
            new AxisCommissioningProjection(
                new AxisSnapshot("axis.y", "Y Axis", AxisState.Idle, 2, 0),
                null,
                HasSelectedAxisStage: false,
                IsRunMode: false,
                IsApplyingProject: false,
                IsValidationBusy: false,
                RuntimeDefinitionDirty: false,
                IsRunning: false,
                ControlOwner: SimulationControlOwner.Definition,
                AutomaticRunActive: false,
                SequenceRunActive: false));

        startCompletion.SetResult(CreateAcceptedResult(new JogAxisCommand("axis.x", AxisJogDirection.Positive)));
        await endJogTask;

        Assert.Equal(1, dispatchCount);
        Assert.Equal("12.500", viewModel.AxisTargetPositionText);
        Assert.False(viewModel.CanJogAxis);
        Assert.False(viewModel.CanMoveAxisAbsolute);
        Assert.False(viewModel.HomeAxisCommand.CanExecute(null));
    }

    [Fact]
    public void DisposeNotifiesCommandsOfFinalAdmission()
    {
        var viewModel = new AxisCommissioningViewModel(
            (command, _) => Task.FromResult(CreateAcceptedResult(command)),
            _ => { });
        viewModel.ApplyProjection(
            new AxisCommissioningProjection(
                new AxisSnapshot("axis.x", "X Axis", AxisState.Idle, 12.5, 0),
                new VirtualAxisDefinition
                {
                    Id = "axis.x",
                    SoftLimitMin = 0,
                    SoftLimitMax = 100,
                    MaxVelocity = 50
                },
                HasSelectedAxisStage: true,
                IsRunMode: true,
                IsApplyingProject: false,
                IsValidationBusy: false,
                RuntimeDefinitionDirty: false,
                IsRunning: true,
                ControlOwner: SimulationControlOwner.Manual,
                AutomaticRunActive: false,
                SequenceRunActive: false));

        var moveNotifications = 0;
        var homeNotifications = 0;
        viewModel.MoveAxisAbsoluteCommand.CanExecuteChanged += (_, _) => moveNotifications++;
        viewModel.HomeAxisCommand.CanExecuteChanged += (_, _) => homeNotifications++;

        viewModel.Dispose();

        Assert.False(viewModel.MoveAxisAbsoluteCommand.CanExecute(null));
        Assert.False(viewModel.HomeAxisCommand.CanExecute(null));
        Assert.Equal(1, moveNotifications);
        Assert.Equal(1, homeNotifications);
    }

    private static SimulationCommandResult CreateAcceptedResult(SimulationCommand command) =>
        new(
            command.CommandId,
            true,
            1,
            TimeSpan.Zero,
            SimulationCommandErrorCode.None,
            null);
}
