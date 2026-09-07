using OpenVisionLab.Machine.Simulation.FaultScenarios;

namespace OpenVisionLab.MachineStudio;

internal static class DirectExeFaultScenarioHost
{
    internal static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var projectPath = DirectExeSmokeArgumentParser.GetArgumentValue(args, "--fault-project");
        var scenarioPath = DirectExeSmokeArgumentParser.GetArgumentValue(args, "--fault-scenario");
        var reportPath = DirectExeSmokeArgumentParser.GetArgumentValue(args, "--fault-report");
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            Console.Error.WriteLine("Missing --fault-project argument.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(scenarioPath))
        {
            Console.Error.WriteLine("Missing --fault-scenario argument.");
            return 2;
        }

        var runner = new DeterministicFaultScenarioHeadlessRunner();
        var report = await runner.RunAsync(projectPath, scenarioPath, reportPath);
        if (!report.IsSuccess)
        {
            Console.Error.WriteLine($"Fault-scenario replay failed: {report.FailureReason}");
            foreach (var error in report.CompilationErrors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        Console.WriteLine(
            $"Fault-scenario replay succeeded: " +
            $"{report.ReplayResult?.CommandResults.Count ?? 0} actions, " +
            $"{report.ReplayResult?.SnapshotHistory.Count ?? 0} snapshots, " +
            $"{report.ReplayResult?.EventHistory.Count ?? 0} events.");
        return 0;
    }
}
