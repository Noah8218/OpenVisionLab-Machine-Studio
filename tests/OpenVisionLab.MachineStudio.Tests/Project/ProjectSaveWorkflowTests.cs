using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectSaveWorkflowTests
{
    [Fact]
    public async Task SavePreparesProjectPersistsFileAndRunsArtifactsInOrder()
    {
        var directory = CreateTestDirectory();
        try
        {
            var project = new MachineProjectDocument { Name = "Before save" };
            var events = new List<string>();
            string? savedPath = null;
            var workflow = CreateWorkflow(
                project,
                prepareProject: currentProject =>
                {
                    events.Add("prepare");
                    currentProject.Name = "After prepare";
                },
                persistScenarioBatchArtifacts: path =>
                {
                    events.Add("scenario");
                    savedPath = path;
                    Assert.True(File.Exists(path));
                },
                persistMultiAxisResult: _ => events.Add("multi-axis"),
                persistVisionEvidence: _ => events.Add("vision"));

            var requestedPath = Path.Combine(directory, "nested", "..", "saved.ovmachine");
            var result = await workflow.SaveAsync(requestedPath);

            Assert.Equal(Path.GetFullPath(requestedPath), result);
            Assert.Equal(result, savedPath);
            Assert.Equal(
                new[] { "prepare", "scenario", "multi-axis", "vision" },
                events);
            var reopened = new ProjectDocumentStore().Load(await File.ReadAllTextAsync(result));
            Assert.Equal("After prepare", reopened.Name);
            Assert.NotEqual(default(DateTimeOffset), reopened.ModifiedAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveFailureDoesNotRunPostSaveArtifactCallbacks()
    {
        var directory = CreateTestDirectory();
        try
        {
            var events = new List<string>();
            var workflow = CreateWorkflow(
                new MachineProjectDocument { Name = "Failed save" },
                prepareProject: _ => events.Add("prepare"),
                persistScenarioBatchArtifacts: _ => events.Add("scenario"),
                persistMultiAxisResult: _ => events.Add("multi-axis"),
                persistVisionEvidence: _ => events.Add("vision"));

            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                workflow.SaveAsync(Path.Combine(directory, "missing", "failed.ovmachine")));

            Assert.Equal(new[] { "prepare" }, events);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveWithReceiptReturnsRevisionIdentityAndContentHash()
    {
        var directory = CreateTestDirectory();
        try
        {
            var project = new MachineProjectDocument { Name = "Receipt" };
            var workflow = CreateWorkflow(
                project,
                prepareProject: _ => { },
                persistScenarioBatchArtifacts: _ => { },
                persistMultiAxisResult: _ => { },
                persistVisionEvidence: _ => { });
            var path = Path.Combine(directory, "receipt.ovmachine");

            var receipt = await workflow.SaveWithReceiptAsync(path, "session-a", 7);

            Assert.Equal("session-a", receipt.SessionId);
            Assert.Equal(7, receipt.Revision);
            Assert.Equal(Path.GetFullPath(path), receipt.SavedPath);
            Assert.Equal(
                ProjectDocumentSession.ComputeContentHash(
                    new ProjectDocumentStore().SerializeForEvidence(project)),
                receipt.ContentHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveWithReceiptAppliesWhenLegacySchemaIsNormalizedByFileStore()
    {
        var directory = CreateTestDirectory();
        try
        {
            var project = new MachineProjectDocument
            {
                Name = "Legacy receipt",
                Schema = "1.5"
            };
            var workflow = CreateWorkflow(
                project,
                prepareProject: _ => { },
                persistScenarioBatchArtifacts: _ => { },
                persistMultiAxisResult: _ => { },
                persistVisionEvidence: _ => { });
            var session = new ProjectDocumentSession(
                new ProjectDocumentStore(),
                project,
                currentPath: null);

            var receipt = await workflow.SaveWithReceiptAsync(
                Path.Combine(directory, "legacy-receipt.ovmachine"),
                session.SessionId,
                session.Revision);

            Assert.True(session.TryApplySave(receipt));
            Assert.Equal(receipt.SavedPath, session.CurrentPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectSaveWorkflow CreateWorkflow(
        MachineProjectDocument project,
        Action<MachineProjectDocument> prepareProject,
        Action<string> persistScenarioBatchArtifacts,
        Action<string> persistMultiAxisResult,
        Action<string> persistVisionEvidence) =>
        new(
            new ProjectDocumentFileStore(),
            new ProjectDocumentStore(),
            () => project,
            prepareProject,
            persistScenarioBatchArtifacts,
            persistMultiAxisResult,
            persistVisionEvidence);

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\project-save-workflow-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
