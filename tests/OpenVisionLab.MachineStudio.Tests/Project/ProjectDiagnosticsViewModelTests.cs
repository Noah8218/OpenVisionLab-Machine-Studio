using System.Text;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectDiagnosticsViewModelTests
{
    [Fact]
    public void NewUnsavedProjectDoesNotClaimSavedBaselineOrHealthyFileDiagnostics()
    {
        var project = new MachineProjectDocument { Name = "Untitled" };
        using var viewModel = new ProjectDiagnosticsViewModel(
            new ProjectDocumentDiagnostics(),
            () => project,
            () => null,
            () => false,
            () => false,
            _ => Task.FromResult(true),
            _ => { });

        Assert.DoesNotContain(
            viewModel.CurrentStateText,
            new[] { "현재 문서는 저장 기준과 일치합니다.", "The current document matches its saved baseline." });
        Assert.DoesNotContain(
            viewModel.SummaryText,
            new[] { "프로젝트 상태가 정상입니다.", "Project health is good." });
        Assert.Contains(
            viewModel.Items,
            item => item.Code == ProjectDocumentDiagnosticCode.NoSavedPath);
    }

    [Fact]
    public void Mch003_LoadedBackupShowsSourceNoticeUntilAnotherTransition()
    {
        var originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.Korean, save: false);

        try
        {
            var project = new MachineProjectDocument { Name = "Recovered" };
            using var viewModel = new ProjectDiagnosticsViewModel(
                new ProjectDocumentDiagnostics(),
                () => project,
                () => @"D:\recovered.ovmachine",
                () => false,
                () => false,
                _ => Task.FromResult(true),
                _ => { });

            viewModel.NotifyProjectLoaded(new ProjectDocumentLoadResult(
                project,
                @"D:\recovered.ovmachine",
                @"D:\recovered.ovmachine.bak",
                ProjectDocumentLoadSource.Backup,
                ProjectDocumentRecoveryReason.PrimaryInvalid));

            Assert.True(viewModel.HasLoadSourceNotice);
            Assert.Contains("다른 이름으로 저장", viewModel.LoadSourceNoticeText, StringComparison.Ordinal);

            viewModel.NotifyProjectLoaded(null);

            Assert.False(viewModel.HasLoadSourceNotice);
            Assert.Equal(string.Empty, viewModel.LoadSourceNoticeText);
        }
        finally
        {
            OpenVisionLanguageService.SetLanguage(originalLanguage, save: false);
        }
    }

    [Fact]
    public async Task PreviewIsReadOnlyAndDirtyDocumentsCannotApplyRecovery()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = await CreateRecoverableProjectAsync(directory);
            await File.WriteAllTextAsync(path, "corrupted primary", Encoding.UTF8);
            var project = new ProjectDocumentFileStore().Load(path + ".bak");
            var statuses = new List<string>();
            using var viewModel = CreateViewModel(
                project,
                path,
                hasUnsavedChanges: true,
                applyRecovery: _ => Task.FromResult(true),
                statuses: statuses);
            var primaryBefore = await File.ReadAllBytesAsync(path);

            viewModel.PreviewRecoveryCommand.Execute(null);

            Assert.True(viewModel.HasRecoveryPreview);
            Assert.False(viewModel.ApplyRecoveryCommand.CanExecute(null));
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
            Assert.Empty(statuses);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyCommandAdmitsOnlyOneInFlightRecovery()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = await CreateRecoverableProjectAsync(directory);
            await File.WriteAllTextAsync(path, "corrupted primary", Encoding.UTF8);
            var project = new ProjectDocumentFileStore().Load(path + ".bak");
            var applyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var applyCount = 0;
            using var viewModel = CreateViewModel(
                project,
                path,
                hasUnsavedChanges: false,
                applyRecovery: async _ =>
                {
                    applyCount++;
                    applyStarted.SetResult(true);
                    await releaseApply.Task;
                    return true;
                },
                statuses: new List<string>());

            viewModel.PreviewRecoveryCommand.Execute(null);
            Assert.True(viewModel.ApplyRecoveryCommand.CanExecute(null));
            viewModel.ApplyRecoveryCommand.Execute(null);
            await applyStarted.Task;
            Assert.False(viewModel.ApplyRecoveryCommand.CanExecute(null));
            viewModel.ApplyRecoveryCommand.Execute(null);
            Assert.Equal(1, applyCount);

            releaseApply.SetResult(true);
            await WaitForAsync(() => !viewModel.HasRecoveryPreview);
            Assert.Equal(1, applyCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelPreviewLeavesFilesUntouchedAndClearsRecoveryState()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = await CreateRecoverableProjectAsync(directory);
            await File.WriteAllTextAsync(path, "corrupted primary", Encoding.UTF8);
            var project = new ProjectDocumentFileStore().Load(path + ".bak");
            var statuses = new List<string>();
            using var viewModel = CreateViewModel(
                project,
                path,
                hasUnsavedChanges: false,
                applyRecovery: _ => Task.FromResult(true),
                statuses: statuses);
            var primaryBefore = await File.ReadAllBytesAsync(path);
            var backupBefore = await File.ReadAllBytesAsync(path + ".bak");

            viewModel.PreviewRecoveryCommand.Execute(null);
            Assert.True(viewModel.HasRecoveryPreview);
            Assert.True(viewModel.CancelRecoveryPreviewCommand.CanExecute(null));

            viewModel.CancelRecoveryPreviewCommand.Execute(null);

            Assert.False(viewModel.HasRecoveryPreview);
            Assert.False(viewModel.ApplyRecoveryCommand.CanExecute(null));
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
            Assert.Equal(backupBefore, await File.ReadAllBytesAsync(path + ".bak"));
            Assert.Empty(statuses);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectDiagnosticsViewModel CreateViewModel(
        MachineProjectDocument project,
        string path,
        bool hasUnsavedChanges,
        Func<ProjectDocumentRecoveryPreview, Task<bool>> applyRecovery,
        List<string> statuses) =>
        new(
            new ProjectDocumentDiagnostics(),
            () => project,
            () => path,
            () => hasUnsavedChanges,
            () => false,
            applyRecovery,
            statuses.Add);

    private static async Task<string> CreateRecoverableProjectAsync(string directory)
    {
        var path = Path.Combine(directory, "recoverable.ovmachine");
        var fileStore = new ProjectDocumentFileStore();
        await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
        await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\project-diagnostics-view-model-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
