using System.Security.Cryptography;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationGeneratedSourceStoreTests
{
    [Theory]
    [InlineData(".pgm")]
    [InlineData(".c3d")]
    public async Task WritesIdenticalProjectOwnedBytesAndRefusesTamperThenRecovers(string extension)
    {
        var root = CreateRoot();
        var project = Path.Combine(root, "recipe.ovmachine");
        await File.WriteAllTextAsync(project, "{}");
        byte[] bytes = [1, 2, 3, 4];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var evidence = new MachineIntegrationFrameEvidence("camera/frame/1", $"assets/generated-capture/{hash}{extension}", hash, bytes.Length);
        var store = new MachineIntegrationGeneratedSourceStore();
        await store.WriteAsync(project, evidence, bytes);
        var file = Path.Combine(root, evidence.SourceRelativePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        await store.WriteAsync(project, evidence, bytes);
        await File.WriteAllBytesAsync(file, [9]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(project, evidence, bytes));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(file));
        File.Delete(file);
        await store.WriteAsync(project, evidence, bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingProjectWrongHashUnsafeNameAndCancellationCannotWriteInput()
    {
        var root = CreateRoot();
        var project = Path.Combine(root, "recipe.ovmachine");
        byte[] bytes = [1, 2];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var evidence = new MachineIntegrationFrameEvidence("frame", $"assets/generated-capture/{hash}.pgm", hash, bytes.Length);
        var store = new MachineIntegrationGeneratedSourceStore();
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.WriteAsync(project, evidence, bytes));
        await File.WriteAllTextAsync(project, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(project, evidence, [3, 4]));
        var unsafeName = new MachineIntegrationFrameEvidence("frame", "assets/user-image.pgm", hash, bytes.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(project, unsafeName, bytes));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(project, evidence, bytes, new CancellationToken(true)));
        Assert.Equal(new[] { project }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        await store.WriteAsync(project, evidence, bytes);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, evidence.SourceRelativePath)));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(@"D:/OpenVisionLab-TestData/Machine/public-readiness-workflow-20261003/generated-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
