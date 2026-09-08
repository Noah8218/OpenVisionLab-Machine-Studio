using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SemiconductorRecipeCopyWorkflowTests
{
    [Fact]
    public async Task CopyLoadsSourceCreatesNewIdentityAndSavesDestination()
    {
        var directory = CreateTestDirectory();
        try
        {
            var sourcePath = Path.Combine(directory, "source.ovmachine");
            var destinationPath = Path.Combine(directory, "nested", "copy.ovmachine");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var source = new MachineProjectDocument
            {
                Id = "source-id",
                Name = "Recipe source",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1)
            };
            await new ProjectDocumentStore().SaveAsync(source, sourcePath);
            var sourceBeforeCopy = new ProjectDocumentStore().Load(
                await File.ReadAllTextAsync(sourcePath));
            var workflow = new SemiconductorRecipeCopyWorkflow(
                new ProjectDocumentStore(),
                () => "same path");

            var result = await workflow.CopyAsync(sourcePath, destinationPath);

            Assert.Equal(Path.GetFullPath(destinationPath), result);
            var copied = new ProjectDocumentStore().Load(
                await File.ReadAllTextAsync(destinationPath));
            Assert.Equal("Recipe source", copied.Name);
            Assert.NotEqual(sourceBeforeCopy.Id, copied.Id);
            Assert.NotEqual(default, copied.CreatedAt);
            Assert.True(copied.CreatedAt > sourceBeforeCopy.CreatedAt);
            Assert.True(copied.ModifiedAt >= copied.CreatedAt);

            var sourceAfterCopy = new ProjectDocumentStore().Load(
                await File.ReadAllTextAsync(sourcePath));
            Assert.Equal(sourceBeforeCopy.Id, sourceAfterCopy.Id);
            Assert.Equal(sourceBeforeCopy.CreatedAt, sourceAfterCopy.CreatedAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CopyRejectsSameSourceAndDestinationBeforeLoading()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "recipe.ovmachine");
            var message = "cannot overwrite recipe";
            var workflow = new SemiconductorRecipeCopyWorkflow(
                new ProjectDocumentStore(),
                () => message);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workflow.CopyAsync(path, Path.Combine(directory, ".", "recipe.ovmachine")));

            Assert.Equal(message, exception.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\semiconductor-recipe-copy-workflow-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
