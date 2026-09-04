using OpenVisionLab.Machine.Core.Projects;
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
            await new ProjectDocumentStore().SaveAsync(
                new MachineProjectDocument { Name = "Loaded project" },
                path);
            MachineProjectDocument? appliedProject = null;
            string? appliedPath = null;
            Exception? failure = null;
            var workflow = CreateWorkflow(
                resolveUnsavedChanges: () => Task.FromResult(true),
                applyOpenedProject: (project, projectPath) =>
                {
                    appliedProject = project;
                    appliedPath = projectPath;
                    return Task.FromResult(true);
                },
                handleLoadFailure: exception => failure = exception);

            Assert.True(await workflow.OpenAsync(path));
            Assert.NotNull(appliedProject);
            Assert.Equal("Loaded project", appliedProject.Name);
            Assert.Equal(Path.GetFullPath(path), appliedPath);
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
                applyOpenedProject: (_, _) =>
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
            await new ProjectDocumentStore().SaveAsync(
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
                applyOpenedProject: (_, _) =>
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

    private static ProjectOpenWorkflow CreateWorkflow(
        Func<Task<bool>> resolveUnsavedChanges,
        Func<MachineProjectDocument, string, Task<bool>> applyOpenedProject,
        Action<Exception> handleLoadFailure) =>
        new(
            new ProjectDocumentStore(),
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
