using System.Security.Cryptography;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.History;
using Xunit;

namespace OpenVisionLab.Machine.Core.Tests;

public sealed class MachineRunRecordFileStoreTests
{
    [Fact]
    public async Task RecordsReopenPerRecipeAndOnlySelectedDeletionRemovesEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "MachineRunRecordTests", Guid.NewGuid().ToString("N"));
        var store = new MachineRunRecordFileStore(root);
        MachineRunRecord first = Record("a"), second = Record("a"), other = Record("b");
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        await store.SaveAsync(other);
        var restarted = new MachineRunRecordFileStore(root);
        var records = await restarted.ReadAsync("a");
        Assert.Empty(records.Errors);
        Assert.Equal(2, records.Records.Count);
        Assert.Equal(first.Captures[0].Pixels, records.Records.Single(record => record.Id == first.Id).Captures[0].Pixels);
        store.Delete("a", first.Id);
        Assert.Equal(second.Id, Assert.Single((await restarted.ReadAsync("a")).Records).Id);
        Assert.Equal(other.Id, Assert.Single((await restarted.ReadAsync("b")).Records).Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(first with { Id = "../../other" }));
    }

    [Fact]
    public async Task CancellationAndConflictingWritesPreserveCommittedBytesAndRetryRecovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "MachineRunRecordTests", Guid.NewGuid().ToString("N"));
        var store = new MachineRunRecordFileStore(root);
        var record = Record("a");
        await store.SaveAsync(record);
        var path = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Single();
        var original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(record with { Outcome = "Stopped" }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Record("a"), cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        await store.SaveAsync(record);
        var valid = Record("a");
        await store.SaveAsync(valid);
        Assert.Equal(2, (await store.ReadAsync("a")).Records.Count);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".json"), "{truncated");
        var read = await store.ReadAsync("a");
        Assert.Equal(2, read.Records.Count);
        Assert.Single(read.Errors);
    }

    [Fact]
    public async Task LockedRecordDeletionFailsWithoutChangingBytesThenRecovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "MachineRunRecordTests", Guid.NewGuid().ToString("N"));
        var store = new MachineRunRecordFileStore(root);
        var record = Record("a");
        await store.SaveAsync(record);
        var path = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Single();
        var original = await File.ReadAllBytesAsync(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.Delete(record.RecipeId, record.Id));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Single((await store.ReadAsync(record.RecipeId)).Records);
        }
        store.Delete(record.RecipeId, record.Id);
        Assert.Empty((await store.ReadAsync(record.RecipeId)).Records);
    }

    [Fact]
    public async Task OriginalSourceHashMustMatchAndMissingImagesAreNeverReconstructed()
    {
        var root = Path.Combine(Path.GetTempPath(), "MachineRunRecordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.png");
        byte[] bytes = [1, 4, 9, 16];
        await File.WriteAllBytesAsync(source, bytes);
        var record = Record("a") with { ProjectPath = Path.Combine(root, "recipe.ovmachine") };
        record = record with { Captures = [record.Captures[0] with { Pixels = null, SourceRelativePath = "source.png", ContentSha256 = Convert.ToHexString(SHA256.HashData(bytes)) }] };
        var store = new MachineRunRecordFileStore(Path.Combine(root, "history"));
        var copied = await store.PreserveSourceImagesAsync(record);
        Assert.Equal(bytes, copied.Captures[0].Pixels);
        await File.WriteAllBytesAsync(source, [99]);
        var stale = await store.PreserveSourceImagesAsync(record);
        Assert.Null(stale.Captures[0].Pixels);
        Assert.NotNull(stale.Captures[0].ImageError);
        await store.SaveAsync(copied);
        Assert.Equal(bytes, Assert.Single((await store.ReadAsync("a")).Records).Captures[0].Pixels);
    }

    private static MachineRunRecord Record(string recipeId)
    {
        var project = new MachineProjectDocument { Id = recipeId, Name = "Stored settings" };
        byte[] pixels = [1, 2, 3, 4];
        return new()
        {
            Id = Guid.NewGuid().ToString("N"), RecipeId = recipeId, RecipeName = project.Name,
            AppliedRecipeJson = new ProjectDocumentStore().Serialize(project), Outcome = "Completed",
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), EndedAtUtc = DateTimeOffset.UtcNow,
            Captures = [new() { AcquisitionId = "acq", CameraId = "camera", Pixels = pixels, ContentSha256 = Convert.ToHexString(SHA256.HashData(pixels)) }]
        };
    }
}
