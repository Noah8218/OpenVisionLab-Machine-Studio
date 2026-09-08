using OpenVisionLab;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class EquipmentCommandDispatcherTests
{
    [Fact]
    public async Task AcceptedAxisCommandUsesExistingStatusAndLogContract()
    {
        OpenVisionLanguageService.Load();
        using var engine = new FixedStepSimulationEngine(
            new SimulationSettings { FixedStep = TimeSpan.FromMilliseconds(1) });
        await engine.StartAsync();
        Assert.True((await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(new SimulationRuntimeConfiguration([], [], [])))).IsAccepted);

        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var dispatcher = new EquipmentCommandDispatcher(
            engine,
            statuses.Add,
            (category, message) => logs.Add((category, message)));

        var result = await dispatcher.DispatchAxisCommandAsync(
            new StartManualControlCommand(),
            "Axis.ActionStartManual");

        Assert.True(result.IsAccepted);
        Assert.Single(statuses);
        Assert.Single(logs);
        Assert.Equal("Motion", logs[0].Category);
        Assert.Contains("CMD-", logs[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedCommandsPreserveEachEquipmentCategory()
    {
        OpenVisionLanguageService.Load();
        using var engine = new FixedStepSimulationEngine(new SimulationSettings());
        var statuses = new List<string>();
        var logs = new List<(string Category, string Message)>();
        var dispatcher = new EquipmentCommandDispatcher(
            engine,
            statuses.Add,
            (category, message) => logs.Add((category, message)));

        var results = await Task.WhenAll(
            dispatcher.DispatchAxisCommandAsync(new PauseCommand(), "Axis.ActionStartManual"),
            dispatcher.DispatchCameraCommandAsync(new PauseCommand(), "Camera.ActionStartManual"),
            dispatcher.DispatchSensorCommandAsync(new PauseCommand(), "Sensor.ActionStartManual"),
            dispatcher.DispatchCylinderCommandAsync(new PauseCommand(), "Cylinder.ActionStartManual"),
            dispatcher.DispatchConveyorCommandAsync(new PauseCommand(), "Conveyor.ActionStartManual"));

        Assert.All(results, result =>
        {
            Assert.False(result.IsAccepted);
            Assert.Equal(SimulationCommandErrorCode.EngineNotStarted, result.ErrorCode);
        });
        Assert.Equal(
            new[] { "Motion", "Camera", "Sensor", "Cylinder", "Conveyor" },
            logs.Select(log => log.Category));
        Assert.Equal(5, statuses.Count);
    }
}
