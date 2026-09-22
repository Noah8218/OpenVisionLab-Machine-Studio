using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectOpenWorkflowTests
{
    [Fact]
    public async Task OpenLoadsProjectAndPassesItToApplicationCallback()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "open.ovmachine");
            await new ProjectDocumentFileStore().SaveAsync(
                new MachineProjectDocument { Name = "Loaded project" },
                path);
            ProjectDocumentLoadResult? appliedLoad = null;
            Exception? failure = null;
            var workflow = CreateWorkflow(
                resolveUnsavedChanges: () => Task.FromResult(true),
                applyOpenedProject: loadResult =>
                {
                    appliedLoad = loadResult;
                    return Task.FromResult(true);
                },
                handleLoadFailure: exception => failure = exception);

            Assert.True(await workflow.OpenAsync(path));
            Assert.NotNull(appliedLoad);
            Assert.Equal("Loaded project", appliedLoad.Document.Name);
            Assert.Equal(Path.GetFullPath(path), appliedLoad.ProjectPath);
            Assert.Equal(ProjectDocumentLoadSource.Primary, appliedLoad.Source);
            Assert.Null(failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidFileUsesFailureCallbackAndDoesNotApplyProject()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "invalid.ovmachine");
            await File.WriteAllTextAsync(path, "{\"schema\":\"1.12\",\"name\":");
            var applyCount = 0;
            Exception? failure = null;
            var workflow = CreateWorkflow(
                resolveUnsavedChanges: () => Task.FromResult(true),
                applyOpenedProject: _ =>
                {
                    applyCount++;
                    return Task.FromResult(true);
                },
                handleLoadFailure: exception => failure = exception);

            Assert.False(await workflow.OpenAsync(path));
            Assert.Equal(0, applyCount);
            Assert.IsType<System.Text.Json.JsonException>(failure);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacementChecksUnsavedChangesBeforeSecondLoadOrApply()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "replace.ovmachine");
            await new ProjectDocumentFileStore().SaveAsync(
                new MachineProjectDocument { Name = "Replacement" },
                path);
            var resolveCount = 0;
            var applyCount = 0;
            var workflow = CreateWorkflow(
                resolveUnsavedChanges: () =>
                {
                    resolveCount++;
                    return Task.FromResult(false);
                },
                applyOpenedProject: _ =>
                {
                    applyCount++;
                    return Task.FromResult(true);
                },
                handleLoadFailure: _ => { });

            Assert.False(await workflow.OpenAsync(path, replaceCurrent: true));
            Assert.Equal(1, resolveCount);
            Assert.Equal(0, applyCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mch003_RecoveredOpenPassesBackupProvenanceWithoutWritingFiles()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "recovered.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup project" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current project" }, path);
            await File.WriteAllTextAsync(path, "corrupted primary");
            var primaryBefore = await File.ReadAllBytesAsync(path);
            var backupBefore = await File.ReadAllBytesAsync(path + ".bak");
            ProjectDocumentLoadResult? appliedLoad = null;

            var workflow = CreateWorkflow(
                resolveUnsavedChanges: () => Task.FromResult(true),
                applyOpenedProject: loadResult =>
                {
                    appliedLoad = loadResult;
                    return Task.FromResult(true);
                },
                handleLoadFailure: _ => { });

            Assert.True(await workflow.OpenAsync(path));
            Assert.NotNull(appliedLoad);
            Assert.Equal("Backup project", appliedLoad.Document.Name);
            Assert.Equal(Path.GetFullPath(path), appliedLoad.ProjectPath);
            Assert.Equal(Path.GetFullPath(path + ".bak"), appliedLoad.SourcePath);
            Assert.Equal(ProjectDocumentLoadSource.Backup, appliedLoad.Source);
            Assert.Equal(ProjectDocumentRecoveryReason.PrimaryInvalid, appliedLoad.RecoveryReason);
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
            Assert.Equal(backupBefore, await File.ReadAllBytesAsync(path + ".bak"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectOpenWorkflow CreateWorkflow(
        Func<Task<bool>> resolveUnsavedChanges,
        Func<ProjectDocumentLoadResult, Task<bool>> applyOpenedProject,
        Action<Exception> handleLoadFailure) =>
        new(
            new ProjectDocumentFileStore(),
            resolveUnsavedChanges,
            applyOpenedProject,
            handleLoadFailure);

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\project-open-workflow-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
