using System;
using System.IO;
using System.Threading.Tasks;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeBatchPersistenceVerifier
{
    public static async Task VerifySaveAndReloadAsync(MainViewModel vm, string projectPath)
    {
        vm.SimulationWorkspace.BatchRepetitionCount = 2;
        vm.SimulationWorkspace.ScenarioDurationCycles = 1200;
        await vm.SaveProjectAsync(projectPath);
        for (var attempt = 0; attempt < 40 && !vm.RunScenarioBatchCommand.CanExecute(null); attempt++)
        {
            await Task.Delay(50);
        }

        var previousBatch = vm.LatestBatchResult;
        vm.RunScenarioBatchCommand.Execute(null);
        for (var attempt = 0;
             attempt < 120 && (vm.IsBatchRunning || ReferenceEquals(vm.LatestBatchResult, previousBatch));
             attempt++)
        {
            await Task.Delay(25);
        }

        if (ReferenceEquals(vm.LatestBatchResult, previousBatch)
            || vm.LatestBatchResult is not { IsComplete: true, IsSuccess: true, CompletedRuns: 2 })
        {
            throw new InvalidOperationException("Persisted repeat validation did not complete successfully.");
        }

        vm.AcceptBatchBaselineCommand.Execute(null);
        if (!File.Exists($"{Path.GetFullPath(projectPath)}.batch-result.json")
            || !File.Exists($"{Path.GetFullPath(projectPath)}.batch-baseline.json"))
        {
            throw new InvalidOperationException("Project-linked batch sidecars were not saved.");
        }

        if (!await vm.OpenProjectAsync(projectPath) || !vm.HasRestoredBatchArtifacts)
        {
            throw new InvalidOperationException("Saved batch evidence did not restore in the same process.");
        }

        if (!vm.SimulationWorkspace.RequireAutomaticCycleCompleted
            || vm.SimulationWorkspace.MinimumCompletedCycles != 1
            || !vm.SimulationWorkspace.RequireNoActiveFaults
            || !vm.SimulationWorkspace.RequireFinalEquipmentState
            || vm.SimulationWorkspace.FinalEquipmentTargetId != "cylinder-1"
            || vm.SimulationWorkspace.FinalEquipmentExpectedState != "Extended"
            || vm.ConditionScenario.IsConfigured)
        {
            throw new InvalidOperationException(
                "Acceptance criteria did not round-trip without auto-running.");
        }

        Console.WriteLine("Batch persistence smoke passed: save, result/baseline sidecars, same-process reload.");
    }
}
