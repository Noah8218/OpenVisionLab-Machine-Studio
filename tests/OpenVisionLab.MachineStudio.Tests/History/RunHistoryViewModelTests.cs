using System.Threading.Channels;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.History;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.Model;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class RunHistoryViewModelTests
{
    private readonly StudioUiTestHost _ui;
    public RunHistoryViewModelTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public Task SelectionCancelAndRejectedApplyPreserveOriginalAndNeverStartARun() => _ui.InvokeTaskAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "RunHistoryViewTests", Guid.NewGuid().ToString("N"));
        var store = new MachineRunRecordFileStore(root);
        var project = new MachineProjectDocument { Id = "a", Name = "At run" };
        var record = Record(project);
        await store.SaveAsync(record);
        project.Name = "Current settings";
        int applyCount = 0;
        bool accept = false;
        using var vm = new RunHistoryViewModel(store, () => project, () => true,
            draft => { applyCount++; if (accept) project = draft; return Task.FromResult(accept); },
            action => { action(); return Task.CompletedTask; });
        await vm.RefreshAsync();
        Assert.Equal(record.Id, vm.Selected!.Record.Id);
        Assert.Contains(record.Id, vm.LatestRunStatusText, StringComparison.Ordinal);
        Assert.Contains(vm.Runs[0].Outcome, vm.LatestRunStatusText, StringComparison.Ordinal);
        Assert.Contains("At run", vm.AppliedSettingsText);
        vm.PrepareRerunCommand.Execute(null);
        Assert.True(vm.HasDraft);
        Assert.Equal(0, applyCount);
        Assert.Equal("Current settings", project.Name);
        vm.CancelDraftCommand.Execute(null);
        Assert.False(vm.HasDraft);
        vm.DeleteCommand.Execute(null);
        vm.CancelDeleteCommand.Execute(null);
        Assert.Single((await store.ReadAsync("a")).Records);
        vm.PrepareRerunCommand.Execute(null);
        await vm.ApplyDraftAsync();
        Assert.True(vm.HasDraft);
        Assert.Equal("Current settings", project.Name);
        accept = true;
        await vm.ApplyDraftAsync();
        Assert.False(vm.HasDraft);
        Assert.Equal("At run", project.Name);
        Assert.Null(project.Simulation.AutomaticRun);
        Assert.Equal(record.AppliedRecipeJson, Assert.Single((await store.ReadAsync("a")).Records).AppliedRecipeJson);
        project = new MachineProjectDocument { Id = "different-recipe" };
        await vm.RefreshAsync();
        Assert.Empty(vm.Runs);
        Assert.Equal(OpenVisionLab.OpenVisionLanguageService.T("R24.Bottom.NoRun"), vm.LatestRunStatusText);
        Assert.Null(vm.Selected);
    });

    [Fact]
    public Task FailedArchiveRemainsPendingUntilValidRetryAndReopen() => _ui.InvokeTaskAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "RunHistoryViewTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blockedRoot = Path.Combine(root, "history");
        await File.WriteAllTextAsync(blockedRoot, "owned fault injection");
        var project = new MachineProjectDocument { Id = "a", Name = "At run" };
        var store = new MachineRunRecordFileStore(blockedRoot);
        using var vm = new RunHistoryViewModel(store, () => project, () => true, _ => Task.FromResult(false),
            action => _ui.InvokeAsync(() => { action(); return true; }));
        var reader = Channel.CreateUnbounded<MachineRunRecord>();
        vm.StartRecording(reader.Reader);
        await reader.Writer.WriteAsync(Record(project));
        reader.Writer.Complete();
        await vm.RecordingCompletion;
        Assert.True(vm.HasPendingSaves);
        Assert.Single(vm.Runs);
        Assert.False(vm.DeleteCommand.CanExecute(null));
        File.Delete(blockedRoot);
        await vm.RetrySaveAsync();
        Assert.False(vm.HasPendingSaves);
        var restarted = new MachineRunRecordFileStore(blockedRoot);
        Assert.Single((await restarted.ReadAsync(project.Id)).Records);
        await vm.RefreshAsync();
        Assert.Single(vm.Runs);
        Assert.True(vm.DeleteCommand.CanExecute(null));
    });

    private static MachineRunRecord Record(MachineProjectDocument project) => new()
    {
        Id = Guid.NewGuid().ToString("N"), RecipeId = project.Id, RecipeName = project.Name,
        AppliedRecipeJson = new ProjectDocumentStore().Serialize(project),
        StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1), EndedAtUtc = DateTimeOffset.UtcNow, Outcome = "Completed"
    };

    [Fact]
    public Task MainWorkflowAppliesHistoricalDraftWithoutRunningAndSavesTheChangedRecipe() => _ui.InvokeTaskAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "RunHistoryMainTests", Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR");
        Environment.SetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR", root);
        try
        {
            var current = new MachineProjectDocument { Id = "main-history", Name = "Current settings" };
            var historical = new MachineProjectDocument { Id = current.Id, Name = "Historical settings" };
            var record = Record(historical);
            var store = new MachineRunRecordFileStore(root);
            await store.SaveAsync(record);
            using var model = new MainViewModel(current);
            model.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
            await WaitForAsync(() => model.SceneSnapshots.Latest is not null && model.RunHistory.Selected is not null);
            model.RunHistory.PrepareRerunCommand.Execute(null);
            Assert.Equal("Current settings", model.RecipeTitleText);
            Assert.False(model.IsRunning);
            await model.RunHistory.ApplyDraftAsync();
            Assert.False(model.RunHistory.HasDraft);
            Assert.Equal("Historical settings", model.RecipeTitleText);
            Assert.True(model.HasUnsavedChanges);
            Assert.False(model.IsRunning);
            Assert.Equal(0, model.SceneSnapshots.Latest!.TickIndex);
            var path = Path.Combine(root, "applied.ovmachine");
            await model.SaveProjectAsync(path);
            Assert.Equal("Historical settings", new ProjectDocumentStore().Load(await File.ReadAllTextAsync(path)).Name);
            Assert.Equal(record.AppliedRecipeJson, Assert.Single((await store.ReadAsync(current.Id)).Records).AppliedRecipeJson);
            Assert.True((await model.RequestCloseAsync()).IsApproved);
        }
        finally { Environment.SetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR", previousRoot); }
    });

    [Fact]
    public Task CloseWaitsForArchiveAndFailedStorageRecoversBeforeClosing() => _ui.InvokeTaskAsync(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "RunHistoryMainTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var blockedRoot = Path.Combine(root, "history");
        await File.WriteAllTextAsync(blockedRoot, "owned fault injection");
        var previousRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR");
        Environment.SetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR", blockedRoot);
        try
        {
            var project = new MachineProjectDocument { Id = "main-close", Name = "Close history", Sequences =
                [new() { Id = "cycle", Name = "Cycle", Steps = [new() { Id = "done", Name = "Done", Action = SequenceStepAction.Complete }] }] };
            using var model = new MainViewModel(project);
            model.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
            await WaitForAsync(() => model.SceneSnapshots.Latest is not null);
            var session = (SimulationSessionCoordinator)typeof(MainViewModel).GetField("_simulationSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(model)!;
            Assert.True((await session.Engine.EnqueueCommandAsync(new StartSequenceCommand("cycle"))).IsAccepted);
            Assert.True((await session.Engine.EnqueueCommandAsync(new StepCommand())).IsAccepted);
            await WaitForAsync(() => model.RunHistory.HasPendingSaves);
            Assert.Contains(OpenVisionLab.OpenVisionLanguageService.T("RunHistory.SaveFailed"), model.StatusMessage);
            Assert.False((await model.RequestCloseAsync()).IsApproved);
            Assert.True(model.RunHistory.HasPendingSaves);
            File.Delete(blockedRoot);
            await model.RunHistory.RetrySaveAsync();
            Assert.False(model.RunHistory.HasPendingSaves);
            Assert.True((await model.RequestCloseAsync()).IsApproved);
            var record = Assert.Single((await new MachineRunRecordFileStore(blockedRoot).ReadAsync(project.Id)).Records);
            Assert.Equal("Completed", record.Outcome);
            Assert.Contains("Close history", record.AppliedRecipeJson);
        }
        finally { Environment.SetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_RUN_HISTORY_DIR", previousRoot); }
    });

    private static async Task WaitForAsync(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(completed(), "The required history state did not arrive within 5 seconds.");
    }
}
