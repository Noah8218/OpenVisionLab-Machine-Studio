using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;

if (args.Length == 4
    && string.Equals(args[0], "--ack", StringComparison.OrdinalIgnoreCase))
{
    return Acknowledge(args[1], args[2], args[3]);
}

if (args.Length == 5
    && string.Equals(args[0], "--run", StringComparison.OrdinalIgnoreCase)
    && string.Equals(args[4], "--approve", StringComparison.OrdinalIgnoreCase))
{
    return RunInspection(args[1], args[2], args[3]);
}

if (args.Length == 3
    && string.Equals(args[0], "--refresh", StringComparison.OrdinalIgnoreCase))
{
    return RefreshResult(args[1], args[2]);
}

Console.Error.WriteLine(
    "Usage: MachineIntegrationInspectionStub --ack <exchangeRoot> <transactionId> <identityPath>");
Console.Error.WriteLine(
    "   or: MachineIntegrationInspectionStub --run <exchangeRoot> <transactionId> <identityPath> --approve");
Console.Error.WriteLine(
    "   or: MachineIntegrationInspectionStub --refresh <exchangeRoot> <transactionId>");
return 2;

static int Acknowledge(
    string exchangeRoot,
    string transactionIdText,
    string identityPath)
{
    try
    {
        var transactionId = ParseTransactionId(transactionIdText);
        var handoff = MachineIntegrationExchange.ReadHandoff(exchangeRoot, transactionId);
        var identity = LoadIdentity(identityPath);
        ValidateSupportedRequest(handoff, identity);
        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc.AddMilliseconds(1),
            identity,
            IntegrationAcknowledgementStatus.Accepted,
            null);
        WriteMessageAtomic(
            exchangeRoot,
            transactionId,
            IntegrationTransactionLayout.AcknowledgementFileName,
            IntegrationContractJson.SerializeCanonical(acknowledgement));
        Console.WriteLine(
            $"ACK_ACCEPTED transaction={handoff.TransactionId:D} handoff={handoff.MessageId:D}");
        return 0;
    }
    catch (IntegrationContractException exception)
    {
        Console.Error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
        return 1;
    }
    catch (Exception exception) when (exception is ArgumentException
        or IOException
        or InvalidDataException
        or JsonException)
    {
        Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

static int RunInspection(
    string exchangeRoot,
    string transactionIdText,
    string identityPath)
{
    try
    {
        var transactionId = ParseTransactionId(transactionIdText);
        var handoff = MachineIntegrationExchange.ReadHandoff(exchangeRoot, transactionId);
        var acknowledgement = MachineIntegrationExchange.ReadAcknowledgement(
            exchangeRoot,
            transactionId);
        var identity = LoadIdentity(identityPath);
        ValidateSupportedRequest(handoff, identity);
        if (acknowledgement.Status != IntegrationAcknowledgementStatus.Accepted)
        {
            throw new IntegrationContractException(
                IntegrationErrorCode.RequestRejected,
                "An inspection can run only after an Accepted Acknowledgement.");
        }

        var transactionDirectory = GetTransactionDirectory(exchangeRoot, transactionId);
        var sourceArtifact = GetArtifact(
            handoff,
            IntegrationArtifactRoles.InspectionSource);
        var sourcePath = ResolveArtifactPath(transactionDirectory, sourceArtifact);
        var image = DecodeP2Mono8(File.ReadAllBytes(sourcePath));
        var brightPixelCount = image.Pixels.Count(pixel => pixel > 200);
        var outcome = brightPixelCount == 0
            ? IntegrationInspectionOutcome.Pass
            : IntegrationInspectionOutcome.Ng;
        var runId = $"stub-{transactionId:N}";
        var runRecordBytes = JsonSerializer.SerializeToUtf8Bytes(
            new StubRunRecord(
                "mch-033-image-stub/1",
                runId,
                transactionId,
                handoff.Context.FrameId,
                image.Width,
                image.Height,
                brightPixelCount,
                outcome.ToString()),
            StubJson.Options);
        var runRecord = new IntegrationArtifactReference(
            IntegrationArtifactRoles.RunRecord,
            runId,
            "artifacts/run-record.json",
            runRecordBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(runRecordBytes)));
        WriteMessageAtomic(
            exchangeRoot,
            transactionId,
            runRecord.RelativePath,
            runRecordBytes);

        var result = new IntegrationResultV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Result,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            acknowledgement.MessageId,
            acknowledgement.CreatedAtUtc.AddMilliseconds(1),
            identity,
            IntegrationResultStatus.Completed,
            outcome,
            runId,
            runRecord,
            IntegrationRunCorrelation.FromContext(handoff.Context),
            [new IntegrationMetric("bright-pixel-count", brightPixelCount, "pixel")],
            [],
            null);
        EnsureValid(IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            acknowledgement,
            result));
        WriteMessageAtomic(
            exchangeRoot,
            transactionId,
            IntegrationTransactionLayout.ResultFileName,
            IntegrationContractJson.SerializeCanonical(result));
        Console.WriteLine(
            $"RESULT_COMPLETED transaction={handoff.TransactionId:D} outcome={outcome} run={runId}");
        return 0;
    }
    catch (IntegrationContractException exception)
    {
        Console.Error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
        return 1;
    }
    catch (Exception exception) when (exception is ArgumentException
        or IOException
        or InvalidDataException
        or JsonException)
    {
        Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

static int RefreshResult(string exchangeRoot, string transactionIdText)
{
    try
    {
        var transactionId = ParseTransactionId(transactionIdText);
        var validated = MachineIntegrationExchange.ReadValidatedResult(
            exchangeRoot,
            transactionId);
        Console.WriteLine(
            $"REFRESH_VALIDATED transaction={validated.Result.TransactionId:D} "
            + $"frame={validated.Handoff.Context.FrameId} "
            + $"outcome={validated.Result.Outcome} "
            + $"run={validated.Result.RunId} "
            + $"inputSha256={validated.Handoff.Context.InputSha256} "
            + $"resultSha256={validated.ResultDocumentSha256}");
        return 0;
    }
    catch (IntegrationContractException exception)
    {
        Console.Error.WriteLine($"{exception.ErrorCode}: {exception.Message}");
        return 1;
    }
    catch (Exception exception) when (exception is ArgumentException
        or IOException
        or InvalidDataException
        or JsonException)
    {
        Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

static IntegrationApplicationIdentity LoadIdentity(string identityPath)
{
    var options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    var identity = JsonSerializer.Deserialize<IntegrationApplicationIdentity>(
        File.ReadAllBytes(Path.GetFullPath(identityPath)),
        options);
    if (identity is null
        || string.IsNullOrWhiteSpace(identity.ApplicationId)
        || string.IsNullOrWhiteSpace(identity.ApplicationVersion)
        || identity.SourceCommit is not { Length: 40 }
        || !identity.SourceCommit.All(Uri.IsHexDigit)
        || identity.SourceState != IntegrationSourceState.Clean)
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.InvalidIdentity,
            "The stub identity must contain a clean application ID, version, and 40-hex source commit.");
    }

    return identity;
}

static void ValidateSupportedRequest(
    IntegrationHandoffV2 handoff,
    IntegrationApplicationIdentity identity)
{
    var validation = IntegrationContractValidator.Validate(handoff);
    EnsureValid(validation);
    if (handoff.Context.Modality != IntegrationInspectionModality.TwoD
        || handoff.Context.InputKind != IntegrationInspectionInputKind.Image
        || !string.Equals(
            handoff.Context.ConsumerBuild.ApplicationId,
            IntegrationApplicationIds.TwoDStudio,
            StringComparison.Ordinal))
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.RequestRejected,
            "MCH-033 supports only the TwoD/Image customer-neutral stub mapping.");
    }

    if (!IdentitiesMatch(identity, handoff.Context.ConsumerBuild))
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.CorrelationMismatch,
            "The stub identity does not match the Handoff ConsumerBuild identity.");
    }

    var inputArtifact = GetArtifact(
        handoff,
        IntegrationArtifactRoles.InspectionSource);
    var recipeArtifact = GetArtifact(
        handoff,
        IntegrationArtifactRoles.InspectionRecipe);
    if (!string.Equals(
            inputArtifact.Sha256,
            handoff.Context.InputSha256,
            StringComparison.OrdinalIgnoreCase)
        || !string.Equals(
            recipeArtifact.Sha256,
            handoff.Context.RecipeSha256,
            StringComparison.OrdinalIgnoreCase))
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.ArtifactHashMismatch,
            "The Handoff context hashes do not match the declared inspection artifacts.");
    }
}

static IntegrationArtifactReference GetArtifact(
    IntegrationHandoffV2 handoff,
    string role) => handoff.Context.Artifacts.SingleOrDefault(artifact =>
        string.Equals(artifact.Role, role, StringComparison.Ordinal))
    ?? throw new IntegrationContractException(
        IntegrationErrorCode.ArtifactMissing,
        $"The Handoff is missing the '{role}' artifact.");

static string ResolveArtifactPath(
    string transactionDirectory,
    IntegrationArtifactReference artifact)
{
    var path = Path.GetFullPath(Path.Combine(
        transactionDirectory,
        artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    var prefix = transactionDirectory.TrimEnd(Path.DirectorySeparatorChar)
                 + Path.DirectorySeparatorChar;
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.UnsafeArtifactPath,
            "The inspection artifact escaped the transaction directory.");
    }

    return path;
}

static void WriteMessageAtomic(
    string exchangeRoot,
    Guid transactionId,
    string relativePath,
    byte[] bytes)
{
    var transactionDirectory = GetTransactionDirectory(exchangeRoot, transactionId);
    var targetPath = Path.GetFullPath(Path.Combine(
        transactionDirectory,
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
    var prefix = transactionDirectory.TrimEnd(Path.DirectorySeparatorChar)
                 + Path.DirectorySeparatorChar;
    if (!targetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.UnsafeArtifactPath,
            "The output path escaped the transaction directory.");
    }

    var directory = Path.GetDirectoryName(targetPath)
        ?? throw new InvalidDataException("The transaction output directory is unavailable.");
    Directory.CreateDirectory(directory);
    if (File.Exists(targetPath))
    {
        throw new IntegrationContractException(
            IntegrationErrorCode.InvalidState,
            $"The output already exists: '{relativePath}'.");
    }

    var temporaryPath = Path.Combine(
        directory,
        $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.SequentialScan))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, targetPath);
    }
    finally
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }
}

static string GetTransactionDirectory(string exchangeRoot, Guid transactionId) =>
    Path.Combine(
        Path.GetFullPath(exchangeRoot),
        IntegrationTransactionLayout.TransactionsDirectoryName,
        transactionId.ToString("D"));

static Guid ParseTransactionId(string value) =>
    Guid.TryParse(value, out var transactionId) && transactionId != Guid.Empty
        ? transactionId
        : throw new ArgumentException(
            "A non-empty transaction ID is required.",
            nameof(value));

static bool IdentitiesMatch(
    IntegrationApplicationIdentity actual,
    IntegrationApplicationIdentity expected) =>
    string.Equals(actual.ApplicationId, expected.ApplicationId, StringComparison.Ordinal)
    && string.Equals(actual.ApplicationVersion, expected.ApplicationVersion, StringComparison.Ordinal)
    && string.Equals(actual.SourceCommit, expected.SourceCommit, StringComparison.OrdinalIgnoreCase)
    && actual.SourceState == expected.SourceState;

static void EnsureValid(IntegrationValidationResult validation)
{
    if (validation.IsValid)
    {
        return;
    }

    var issue = validation.Issues[0];
    throw new IntegrationContractException(issue.Code, issue.Message);
}

static P2Mono8Image DecodeP2Mono8(ReadOnlySpan<byte> bytes)
{
    var reader = new PgmTokenReader(bytes);
    if (!string.Equals(reader.Read("magic"), "P2", StringComparison.Ordinal))
    {
        throw new InvalidDataException("MCH-033 accepts P2 PGM images only.");
    }

    var width = ReadPositiveInt(ref reader, "width");
    var height = ReadPositiveInt(ref reader, "height");
    var maxValue = ReadPositiveInt(ref reader, "max value");
    if (maxValue > byte.MaxValue)
    {
        throw new InvalidDataException("MCH-033 P2 max value must fit Mono8.");
    }

    var pixels = new byte[checked(width * height)];
    for (var index = 0; index < pixels.Length; index++)
    {
        var value = ReadInt(ref reader, "pixel");
        if (value < 0 || value > maxValue)
        {
            throw new InvalidDataException("MCH-033 P2 pixel is outside the declared range.");
        }

        pixels[index] = (byte)value;
    }

    if (reader.TryRead(out _))
    {
        throw new InvalidDataException("MCH-033 P2 contains more pixels than declared.");
    }

    return new(width, height, pixels);
}

static int ReadPositiveInt(ref PgmTokenReader reader, string name)
{
    var value = ReadInt(ref reader, name);
    return value > 0
        ? value
        : throw new InvalidDataException($"MCH-033 P2 {name} must be positive.");
}

static int ReadInt(ref PgmTokenReader reader, string name) =>
    int.TryParse(
        reader.Read(name),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var value)
        ? value
        : throw new InvalidDataException($"MCH-033 P2 {name} is not an integer.");

sealed record P2Mono8Image(int Width, int Height, byte[] Pixels);

sealed record StubRunRecord(
    string SchemaVersion,
    string RunId,
    Guid TransactionId,
    string FrameId,
    int Width,
    int Height,
    int BrightPixelCount,
    string Outcome);

internal static class StubJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

ref struct PgmTokenReader
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
        : throw new InvalidDataException($"MCH-033 P2 is missing the {name} token.");

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
                    throw new InvalidDataException("MCH-033 P2 contains non-ASCII data.");
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
