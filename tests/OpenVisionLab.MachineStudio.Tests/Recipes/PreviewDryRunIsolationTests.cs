using System.Security.Cryptography;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class PreviewDryRunIsolationTests
{
    [Fact]
    public async Task PreviewAndDryRunPreserveLiveRuntimeDirtyStateAndProjectSidecarBytes()
    {
        OpenVisionLanguageService.Load();
        var project = LoadTransferCell();
        var root = Path.Combine(
            TestStorage.RootPath,
            "mch-013-preview-dry-run-isolation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "main.ovmachine");
        var sidecarPath = Path.Combine(root, "main.vision.json");

        try
        {
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(project, projectPath);
            await File.WriteAllTextAsync(sidecarPath, "{\"frameHash\":\"mch-013\"}");
            var projectHash = HashFile(projectPath);
            var sidecarHash = HashFile(sidecarPath);
            var projectBefore = new ProjectDocumentStore().SerializeForEvidence(project);
            var statuses = new List<string>();
            var logs = new List<(string Category, string Message)>();

            var compilation = new MachineProjectRuntimeCompiler(TimeSpan.FromMilliseconds(5))
                .Compile(project);
            Assert.True(compilation.IsSuccess, string.Join("; ", compilation.Errors.Select(error => error.Message)));

            using var engine = new FixedStepSimulationEngine(new SimulationSettings
            {
                FixedStep = TimeSpan.FromMilliseconds(5),
                TimeScale = compilation.Configuration!.TimeScale ?? 1
            });
            await engine.StartAsync();
            try
            {
                var configured = await engine.EnqueueCommandAsync(
                    new ConfigureRuntimeCommand(compilation.Configuration!, "main-project"));
                Assert.True(configured.IsAccepted, configured.Detail);
                var beforeSnapshot = engine.CurrentSnapshot;
                var beforeTraceHash = engine.CreateCommandTracePackage().TraceHash;
                var beforeEventCount = engine.EventJournal.TotalEventCount;
                var workflow = new RecipeConnectionSimulationWorkflow(
                    () => project,
                    _ => { },
                    statuses.Add,
                    (category, message) => logs.Add((category, message)));

                var preview = await workflow.RunSequenceStepPreviewAsync(
                    "auto-transfer-cycle",
                    "extend-stopper",
                    "cylinder-1");
                var dryRun = await workflow.RunRecipeDryRunAsync("auto-transfer-cycle");

                Assert.True(preview.IsCompleted, preview.Detail);
                Assert.True(dryRun.IsCompleted, dryRun.Detail);
                Assert.Same(beforeSnapshot, engine.CurrentSnapshot);
                Assert.Equal(beforeTraceHash, engine.CreateCommandTracePackage().TraceHash);
                Assert.Equal(beforeEventCount, engine.EventJournal.TotalEventCount);
                Assert.Equal(projectBefore, new ProjectDocumentStore().SerializeForEvidence(project));
                Assert.NotEmpty(statuses);
                Assert.NotEmpty(logs);
                Assert.Equal(projectHash, HashFile(projectPath));
                Assert.Equal(sidecarHash, HashFile(sidecarPath));
            }
            finally
            {
                await engine.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static MachineProjectDocument LoadTransferCell() =>
        new ProjectDocumentFileStore().Load(Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine"));

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
