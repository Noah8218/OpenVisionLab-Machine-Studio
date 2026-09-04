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
}
