using System.Reflection;
using System.Security.Cryptography;
using OpenVisionLab.Integration.Contracts;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class BuildIdentityTests
{
    private const string ApplicationVersion = "0.1.0-test";
    private const string SourceCommit =
        "1111111111111111111111111111111111111111";

    [Fact]
    public void MissingRuntimeManifest_FailsClosed()
    {
        var directory = CreateTestDirectory();
        try
        {
            var exception = Assert.Throws<IntegrationContractException>(() =>
                BuildIdentity.LoadQualifiedIntegrationIdentity(
                    TestAssembly,
                    Path.Combine(
                        directory,
                        IntegrationRuntimeBuildManifestContract.FileName)));

            Assert.Equal(IntegrationErrorCode.ArtifactMissing, exception.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TamperedEntryAssemblyHash_FailsClosed()
    {
        var directory = CreateTestDirectory();
        try
        {
            var original = CreateManifest();
            var manifest = original with
            {
                EntryAssembly = original.EntryAssembly with
                {
                    Sha256 = new string('0', 64)
                }
            };
            var manifestPath = WriteManifest(directory, manifest);

            var exception = Assert.Throws<IntegrationContractException>(() =>
                BuildIdentity.LoadQualifiedIntegrationIdentity(
                    TestAssembly,
                    manifestPath));

            Assert.Equal(
                IntegrationErrorCode.ArtifactHashMismatch,
                exception.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DirtyRuntimeIdentity_FailsClosed()
    {
        var directory = CreateTestDirectory();
        try
        {
            var manifestPath = WriteManifest(directory, CreateManifest());

            var exception = Assert.Throws<IntegrationContractException>(() =>
                BuildIdentity.LoadQualifiedIntegrationIdentity(
                    TestAssembly,
                    manifestPath));

            Assert.Equal(IntegrationErrorCode.InvalidIdentity, exception.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static Assembly TestAssembly => typeof(BuildIdentityTests).Assembly;

    private static IntegrationRuntimeBuildManifest CreateManifest()
    {
        var assemblyPath = TestAssembly.Location;
        using var stream = File.OpenRead(assemblyPath);
        return new(
            IntegrationRuntimeBuildManifestContract.SchemaVersion,
            new(
                IntegrationApplicationIds.MachineStudio,
                ApplicationVersion,
                SourceCommit,
                IntegrationSourceState.Dirty),
            new(
                Path.GetFileName(assemblyPath),
                new FileInfo(assemblyPath).Length,
                Convert.ToHexString(SHA256.HashData(stream))));
    }

    private static string WriteManifest(
        string directory,
        IntegrationRuntimeBuildManifest manifest)
    {
        var path = Path.Combine(
            directory,
            IntegrationRuntimeBuildManifestContract.FileName);
        File.WriteAllBytes(
            path,
            IntegrationContractJson.SerializeCanonical(manifest));
        return path;
    }

    private static string CreateTestDirectory()
    {
        var physicalRoot = Directory.Exists(@"D:\")
            ? @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\unit"
            : Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-Studio");
        var directory = Path.Combine(
            physicalRoot,
            $"runtime-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
