using System.Security.Cryptography;
using OpenVisionLab.Machine.Infrastructure.Vision;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>Materializes immutable generated input outside the simulation tick.</summary>
public sealed class MachineIntegrationGeneratedSourceStore
{
    public async Task WriteAsync(string projectPath, MachineIntegrationFrameEvidence evidence, byte[] payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        var ownedPayload = (byte[])payload.Clone();
        if (!File.Exists(projectPath)) throw new FileNotFoundException("Save the project before exporting a generated capture.", projectPath);
        if (ownedPayload.LongLength != evidence.ContentLength
            || !Convert.ToHexString(SHA256.HashData(ownedPayload)).Equals(evidence.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated capture bytes do not match their acquisition evidence.");
        var extension = Path.GetExtension(evidence.SourceRelativePath);
        if (extension is not (".pgm" or ".c3d")
            || evidence.SourceRelativePath != $"assets/generated-capture/{evidence.ContentSha256}{extension}")
            throw new ArgumentException("Generated capture path must be its content-addressed project asset.");

        var resolver = new ProjectAssetPathResolver(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        var asset = resolver.ResolveFileForWrite(evidence.SourceRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(asset.FullPath)!);
        asset = resolver.ResolveFileForWrite(evidence.SourceRelativePath);
        if (File.Exists(asset.FullPath))
        {
            VerifyExisting(asset.FullPath, evidence);
            return;
        }

        var temporaryPath = asset.FullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, ownedPayload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            VerifyExisting(temporaryPath, evidence);
            resolver.ResolveFileForWrite(evidence.SourceRelativePath);
            try { File.Move(temporaryPath, asset.FullPath); }
            catch (IOException) when (File.Exists(asset.FullPath)) { VerifyExisting(asset.FullPath, evidence); }
            VerifyExisting(asset.FullPath, evidence);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void VerifyExisting(string path, MachineIntegrationFrameEvidence evidence)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != evidence.ContentLength
            || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(evidence.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An existing generated capture no longer matches its content identity.");
    }
}
