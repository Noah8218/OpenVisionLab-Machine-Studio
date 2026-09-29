using System.Security.Cryptography;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class Mch027ProjectSchemaReadMatrixTests
{
    private const string TestRoot = @"D:\OpenVisionLab-TestData\Machine\mch-027-schema-read-20260916";

    [Fact]
    public async Task SourceSampleSchemas_LoadReadOnlyAndExplicitSaveAsReopenWithoutChangingOriginal()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sampleRoot = Path.Combine(repositoryRoot, "samples");
        var sourcePaths = Directory
            .EnumerateFiles(sampleRoot, "*.ovmachine", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.NotEmpty(sourcePaths);

        var saveAsRoot = Path.Combine(TestRoot, "save-as");
        Directory.CreateDirectory(saveAsRoot);
        var documentStore = new ProjectDocumentStore();
        var fileStore = new ProjectDocumentFileStore(documentStore);
        var observedSchemas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourcePath in sourcePaths)
        {
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var originalHash = Convert.ToHexString(SHA256.HashData(originalBytes));
            var loaded = await fileStore.LoadAsync(sourcePath);
            observedSchemas.Add(loaded.Schema);

            var saveAsPath = Path.Combine(saveAsRoot, Path.GetRelativePath(sampleRoot, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(saveAsPath)!);
            await fileStore.SaveAsync(loaded, saveAsPath);
            var reopened = await fileStore.LoadAsync(saveAsPath);

            Assert.Equal(MachineProjectDocument.CurrentSchema, reopened.Schema);
            Assert.Equal(loaded.Id, reopened.Id);
            Assert.Equal(loaded.Name, reopened.Name);
            Assert.Equal(originalHash, Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(sourcePath))));
        }

        Assert.Contains("1.5", observedSchemas);
        Assert.Contains("1.11", observedSchemas);
        Assert.All(observedSchemas, schema =>
            Assert.True(Version.TryParse(schema, out var parsed)
                && parsed <= Version.Parse(MachineProjectDocument.CurrentSchema)));
    }

    private static string FindRepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "OpenVisionLab.MachineStudio.sln"))
                && Directory.Exists(Path.Combine(candidate.FullName, "samples")))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Machine Studio repository root from the test output directory.");
    }
}
