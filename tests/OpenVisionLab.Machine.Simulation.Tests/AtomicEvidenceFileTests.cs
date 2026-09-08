using System.Text;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class AtomicEvidenceFileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CreateOrReplacePreservesWriterBytesAndPublishesOnlyAfterWrite(bool existing, bool bom)
    {
        var path = CreatePath();
        if (existing) File.WriteAllText(path, "previous evidence");
        const string json = "{\"note\":\"시운전 α\",\"value\":1}\n";
        var encoding = new UTF8Encoding(bom);

        AtomicEvidenceFile.Write(path, temporaryPath =>
        {
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(temporaryPath));
            Assert.NotEqual(path, temporaryPath);
            File.WriteAllText(temporaryPath, json, encoding);
            if (existing) Assert.Equal("previous evidence", File.ReadAllText(path));
            else Assert.False(File.Exists(path));
        });

        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray(), File.ReadAllBytes(path));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedWritePreservesTargetAndCleansOnlyItsOwnTemporaryFile(bool writePartialFile)
    {
        var path = CreatePath();
        File.WriteAllText(path, "previous evidence");
        var otherTemporaryPath = path + ".other.tmp";
        File.WriteAllText(otherTemporaryPath, "another operation");
        var failure = new IOException("Controlled write failure.");
        string? attemptedTemporaryPath = null;

        var actual = Assert.Throws<IOException>(() => AtomicEvidenceFile.Write(path, temporaryPath =>
        {
            attemptedTemporaryPath = temporaryPath;
            if (writePartialFile) File.WriteAllText(temporaryPath, "incomplete evidence");
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.NotNull(attemptedTemporaryPath);
        Assert.False(File.Exists(attemptedTemporaryPath));
        Assert.Equal("previous evidence", File.ReadAllText(path));
        Assert.Equal("another operation", File.ReadAllText(otherTemporaryPath));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    }

    [Fact]
    public void FailedReplacementPreservesDestinationAndCleansWrittenTemporaryFile()
    {
        var path = CreatePath();
        Directory.CreateDirectory(path);
        var existingChild = Path.Combine(path, "existing.txt");
        File.WriteAllText(existingChild, "preserve directory contents");

        var failure = Record.Exception(() => AtomicEvidenceFile.Write(path,
            temporaryPath => File.WriteAllText(temporaryPath, "new evidence")));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal("preserve directory contents", File.ReadAllText(existingChild));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task OverlappingWritesKeepIndependentTemporaryFilesAndCompletePayloads()
    {
        var path = CreatePath();
        File.WriteAllText(path, "previous evidence");
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? firstTemporaryPath = null;
        var first = Task.Run(() => AtomicEvidenceFile.Write(path, temporaryPath =>
        {
            firstTemporaryPath = temporaryPath;
            File.WriteAllText(temporaryPath, "first complete payload");
            written.SetResult();
            release.Task.GetAwaiter().GetResult();
        }));
        try
        {
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AtomicEvidenceFile.Write(path, temporaryPath =>
            {
                Assert.NotEqual(firstTemporaryPath, temporaryPath);
                File.WriteAllText(temporaryPath, "second complete payload");
            });
            Assert.Equal("second complete payload", File.ReadAllText(path));
            Assert.True(File.Exists(firstTemporaryPath));
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal("first complete payload", File.ReadAllText(path));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)));
    }

    private static string CreatePath()
    {
        var directory = Path.Combine(TestStorage.RootPath, "atomic-evidence-file", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "evidence.json");
    }
}
