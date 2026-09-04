using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.Models.Simulation;
using OpenVisionLab.MachineStudio.ViewModel.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SimulationScenarioProfileTests
{
    [Fact]
    public void Version2Profile_AllTuningFieldsReachEngineProfile()
    {
        string path = WriteProfile(
            """
            {
              "schemaVersion": 2,
              "profileId": "custom-recovery",
              "name": "Custom Recovery",
              "description": "Engine-backed tuning only.",
              "mode": "recovery",
              "seed": 8123,
              "initialState": "recovering",
              "minimumStateTicks": 37,
              "jitterTicks": 9
            }
            """);

        Assert.True(
            SimulationScenarioProfile.TryLoadFromJson(path, out var profile, out var error),
            error);
        using var workspace = new SimulationWorkspaceViewModel
        {
            SelectedScenarioProfile = profile!,
            ScenarioDurationCycles = 120
        };

        DeterministicConditionScenarioProfile engineProfile =
            workspace.BuildEngineProfile("cell-1");

        Assert.Equal("custom-recovery", engineProfile.ScenarioId);
        Assert.Equal(8123, engineProfile.Seed);
        Assert.Equal(120, engineProfile.DurationTicks);
        Assert.Equal(DeterministicConditionState.Recovering, engineProfile.InitialState);
        Assert.Equal(37, engineProfile.MinimumStateTicks);
        Assert.Equal(9, engineProfile.JitterTicks);
    }

    [Fact]
    public void Version1Profile_IsRejectedWithSupportedSchema()
    {
        string path = WriteProfile(
            """
            {
              "schemaVersion": 1,
              "profileId": "legacy",
              "name": "Legacy",
              "mode": "normal",
              "minimumStateTicks": 20,
              "jitterTicks": 0
            }
            """);

        bool loaded = SimulationScenarioProfile.TryLoadFromJson(path, out _, out var error);

        Assert.False(loaded);
        Assert.Contains("Unsupported scenario profile schema '1'", error, StringComparison.Ordinal);
        Assert.Contains("Supported schema is 2", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedRichTuningField_IsRejectedAndExposedByWorkspace()
    {
        string path = WriteProfile(
            """
            {
              "schemaVersion": 2,
              "profileId": "unsupported-rich-profile",
              "name": "Unsupported",
              "mode": "fault",
              "initialState": "degraded",
              "minimumStateTicks": 8,
              "jitterTicks": 3,
              "baseRuntime": { "warningChanceMultiplier": 2.0 }
            }
            """);
        using var workspace = new SimulationWorkspaceViewModel
        {
            ScenarioProfilePath = path
        };

        workspace.LoadScenarioProfileCommand.Execute(null);

        Assert.NotNull(workspace.ScenarioProfileLoadError);
        Assert.Contains("baseRuntime", workspace.ScenarioProfileLoadError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(workspace.ScenarioProfileLoadError, workspace.LoadScenarioProfileTooltip);
        Assert.Equal("normal", workspace.SelectedScenarioProfile.ProfileId);
    }

    private static string WriteProfile(string json)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-Studio", "scenario-profile-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
