using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.Machine.Vision.Models;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class Mch029ImageFixtureTests
{
    private const string FixtureRelativeRoot = "samples/VisionInspectionCell/assets/mch-029";

    [Fact]
    public async Task NormalAndBadFixturesDecodeWithStableMetadataPixelsAndIdentity()
    {
        var manifest = LoadManifest();
        Assert.Equal("openvisionlab.machine/vision-fixture/1", manifest.Schema);
        Assert.Equal("P2-Mono8", manifest.Decoder);
        Assert.Equal(2, manifest.Fixtures.Count);

        var normal = DecodeFixture(manifest.Get("normal"));
        var bad = DecodeFixture(manifest.Get("bad"));

        Assert.Equal((4, 3, 4, "Mono8"), (normal.Width, normal.Height, normal.Stride, normal.PixelFormat));
        Assert.Equal((4, 3, 4, "Mono8"), (bad.Width, bad.Height, bad.Stride, bad.PixelFormat));
        Assert.Equal(12, normal.Pixels.Length);
        Assert.Equal(12, bad.Pixels.Length);
        Assert.Equal("no-bright-patch", normal.Spec.ExpectedFeature);
        Assert.Equal("bright-center-patch", bad.Spec.ExpectedFeature);
        Assert.DoesNotContain(normal.Pixels, pixel => pixel > normal.Spec.BrightPixelThreshold);
        Assert.Equal(2, bad.Pixels.Count(pixel => pixel > bad.Spec.BrightPixelThreshold));
        Assert.True(bad.Pixels[5] > bad.Spec.BrightPixelThreshold);
        Assert.True(bad.Pixels[6] > bad.Spec.BrightPixelThreshold);

        var repositoryRoot = FindRepositoryRoot();
        foreach (var fixture in new[] { normal, bad })
        {
            var relativePath = $"{FixtureRelativeRoot}/{fixture.Spec.File}";
            var source = new ProjectRelativeSingleImageSource(
                repositoryRoot,
                relativePath,
                fixture.Width,
                fixture.Height,
                fixture.PixelFormat);
            var frame = await source.AcquireAsync(CreateContext(fixture.Spec.Id));

            Assert.Equal(fixture.Spec.Sha256, frame.ContentSha256);
            Assert.Equal(fixture.Spec.ByteLength, frame.ContentLength);
            Assert.Equal(fixture.Width, frame.Width);
            Assert.Equal(fixture.Height, frame.Height);
            Assert.Equal(fixture.PixelFormat, frame.PixelFormat);
        }
    }

    [Fact]
    public void TruncatedFixtureIsRejectedByTheDecoder()
    {
        var spec = LoadManifest().Get("normal");
        var bytes = File.ReadAllBytes(GetFixturePath(spec));
        var truncated = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(bytes)[..^" 10".Length]);

        using var fixture = new TemporaryFixture();
        var path = fixture.Write("normal-truncated.pgm", truncated);

        var exception = Assert.Throws<InvalidDataException>(() =>
            DecodeFixture(spec, path, verifyHash: false));

        Assert.Contains("pixel", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataAndUnsupportedFormatAreRejected()
    {
        var spec = LoadManifest().Get("normal");
        var path = GetFixturePath(spec);

        var wrongDimensions = spec with { Width = 5, Stride = 5 };
        var dimensionException = Assert.Throws<InvalidDataException>(() =>
            DecodeFixture(wrongDimensions, path));
        Assert.Contains("metadata", dimensionException.Message, StringComparison.OrdinalIgnoreCase);

        var wrongFormat = spec with { PixelFormat = "Mono16" };
        var formatException = Assert.Throws<InvalidDataException>(() =>
            DecodeFixture(wrongFormat, path));
        Assert.Contains("pixel format", formatException.Message, StringComparison.OrdinalIgnoreCase);

        var bytes = File.ReadAllBytes(path);
        bytes[1] = (byte)'5';
        using var fixture = new TemporaryFixture();
        var unsupportedPath = fixture.Write("normal-p5.pgm", bytes);
        var decoderException = Assert.Throws<InvalidDataException>(() =>
            DecodeFixture(spec, unsupportedPath, verifyHash: false));
        Assert.Contains("P2", decoderException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HashMutationIsRejectedBeforeAChangedFixtureCanBeUsed()
    {
        var spec = LoadManifest().Get("normal");
        var bytes = File.ReadAllBytes(GetFixturePath(spec));
        var lastZero = Array.LastIndexOf(bytes, (byte)'0');
        Assert.True(lastZero >= 0);
        bytes[lastZero] = (byte)'1';

        using var fixture = new TemporaryFixture();
        var path = fixture.Write("normal-mutated.pgm", bytes);

        var exception = Assert.Throws<InvalidDataException>(() => DecodeFixture(spec, path));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    private static FixtureManifest LoadManifest()
    {
        var path = Path.Combine(FindRepositoryRoot(), FixtureRelativeRoot, "manifest.json");
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return manifest ?? throw new InvalidDataException("MCH-029 fixture manifest is empty.");
    }

    private static DecodedFixture DecodeFixture(FixtureSpec spec, string? path = null, bool verifyHash = true)
    {
        path ??= GetFixturePath(spec);
        var bytes = File.ReadAllBytes(path);
        if (verifyHash)
        {
            if (bytes.Length != spec.ByteLength)
            {
                throw new InvalidDataException($"Fixture byte length does not match manifest for '{spec.Id}'.");
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, spec.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Fixture SHA-256 does not match manifest for '{spec.Id}'.");
            }
        }

        var decoded = P2Mono8Decoder.Decode(bytes);
        if (!string.Equals(decoded.PixelFormat, spec.PixelFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Fixture pixel format metadata does not match manifest for '{spec.Id}'.");
        }

        if (decoded.Width != spec.Width
            || decoded.Height != spec.Height
            || decoded.Stride != spec.Stride)
        {
            throw new InvalidDataException($"Fixture metadata does not match manifest for '{spec.Id}'.");
        }

        return new DecodedFixture(spec, decoded.Width, decoded.Height, decoded.Stride, decoded.PixelFormat, decoded.Pixels);
    }

    private static string GetFixturePath(FixtureSpec spec) =>
        Path.Combine(FindRepositoryRoot(), FixtureRelativeRoot, spec.File);

    private static string FindRepositoryRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("MACHINE_STUDIO_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var resolvedRoot = Path.GetFullPath(configuredRoot.Trim());
            if (File.Exists(Path.Combine(resolvedRoot, "OpenVisionLab.MachineStudio.sln")))
            {
                return resolvedRoot;
            }
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenVisionLab.MachineStudio.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Machine Studio repository root.");
    }

    private static VirtualAcquisitionContext CreateContext(string fixtureId) => new(
        $"mch-029/{fixtureId}",
        "cam-mch-029",
        "mch-029",
        simulationTick: 0,
        simulationTime: TimeSpan.Zero,
        seed: 29029,
        new Dictionary<string, double>(StringComparer.Ordinal));

    private sealed class FixtureManifest
    {
        public string Schema { get; init; } = string.Empty;

        public string Decoder { get; init; } = string.Empty;

        public List<FixtureSpec> Fixtures { get; init; } = [];

        public FixtureSpec Get(string id) => Fixtures.Single(fixture =>
            string.Equals(fixture.Id, id, StringComparison.Ordinal));
    }

    private sealed record FixtureSpec
    {
        public string Id { get; init; } = string.Empty;

        public string File { get; init; } = string.Empty;

        public int ByteLength { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int Stride { get; init; }

        public string PixelFormat { get; init; } = string.Empty;

        public string ExpectedFeature { get; init; } = string.Empty;

        public int BrightPixelThreshold { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed record DecodedFixture(
        FixtureSpec Spec,
        int Width,
        int Height,
        int Stride,
        string PixelFormat,
        byte[] Pixels);

    private sealed record DecodedPgm(int Width, int Height, int Stride, string PixelFormat, byte[] Pixels);

    private static class P2Mono8Decoder
    {
        public static DecodedPgm Decode(ReadOnlySpan<byte> bytes)
        {
            var reader = new PgmTokenReader(bytes);
            if (!string.Equals(reader.Read("magic"), "P2", StringComparison.Ordinal))
            {
                throw new InvalidDataException("MCH-029 decoder accepts P2 PGM only.");
            }

            var width = ReadPositiveInt(ref reader, "width");
            var height = ReadPositiveInt(ref reader, "height");
            var maxValue = ReadPositiveInt(ref reader, "max value");
            if (maxValue > byte.MaxValue)
            {
                throw new InvalidDataException("MCH-029 P2 max value must fit Mono8.");
            }

            var pixelCount = checked(width * height);
            var pixels = new byte[pixelCount];
            for (var index = 0; index < pixels.Length; index++)
            {
                var value = ReadInt(ref reader, "pixel");
                if (value < 0 || value > maxValue)
                {
                    throw new InvalidDataException("MCH-029 P2 pixel is outside the declared range.");
                }

                pixels[index] = (byte)value;
            }

            if (reader.TryRead(out _))
            {
                throw new InvalidDataException("MCH-029 P2 contains more pixels than its metadata declares.");
            }

            return new DecodedPgm(width, height, width, "Mono8", pixels);
        }

        private static int ReadPositiveInt(ref PgmTokenReader reader, string name)
        {
            var value = ReadInt(ref reader, name);
            if (value <= 0)
            {
                throw new InvalidDataException($"MCH-029 P2 {name} must be positive.");
            }

            return value;
        }

        private static int ReadInt(ref PgmTokenReader reader, string name)
        {
            var token = reader.Read(name);
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidDataException($"MCH-029 P2 {name} is not an integer.");
            }

            return value;
        }
    }

    private ref struct PgmTokenReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _index;

        public PgmTokenReader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _index = 0;
        }

        public string Read(string name) => TryRead(out var token)
            ? token
            : throw new InvalidDataException($"MCH-029 P2 is missing the {name} token.");

        public bool TryRead(out string token)
        {
            while (true)
            {
                while (_index < _bytes.Length && IsWhitespace(_bytes[_index]))
                {
                    _index++;
                }

                if (_index >= _bytes.Length)
                {
                    token = string.Empty;
                    return false;
                }

                if (_bytes[_index] == (byte)'#')
                {
                    while (_index < _bytes.Length && _bytes[_index] is not ((byte)'\r' or (byte)'\n'))
                    {
                        _index++;
                    }

                    continue;
                }

                var start = _index;
                while (_index < _bytes.Length
                    && !IsWhitespace(_bytes[_index])
                    && _bytes[_index] != (byte)'#')
                {
                    if (_bytes[_index] > 0x7F)
                    {
                        throw new InvalidDataException("MCH-029 P2 contains non-ASCII data.");
                    }

                    _index++;
                }

                token = Encoding.ASCII.GetString(_bytes[start.._index]);
                return true;
            }
        }

        private static bool IsWhitespace(byte value) => value is (byte)' '
            or (byte)'\t'
            or (byte)'\r'
            or (byte)'\n'
            or (byte)'\f';
    }

    private sealed class TemporaryFixture : IDisposable
    {
        public TemporaryFixture()
        {
            Root = Path.Combine(
                TestStorage.RootPath,
                "mch-029-image-fixture",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string fileName, byte[] bytes)
        {
            var path = Path.Combine(Root, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
