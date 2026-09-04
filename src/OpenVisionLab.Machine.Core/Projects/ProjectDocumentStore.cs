using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class ProjectDocumentStore
{
    private static readonly Version CurrentSchemaVersion = Version.Parse(MachineProjectDocument.CurrentSchema);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Save(MachineProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var modifiedAt = DateTimeOffset.UtcNow;
        var json = SerializeForSave(document, modifiedAt);
        ApplySaveMetadata(document, modifiedAt);
        return json;
    }

    public string Serialize(MachineProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public string SerializeForEvidence(MachineProjectDocument document)
    {
        var root = JsonNode.Parse(Serialize(document))?.AsObject()
            ?? throw new InvalidOperationException("Failed to serialize project evidence.");
        root.Remove("modifiedAt");
        return root.ToJsonString(Options);
    }

    public MachineProjectDocument Load(string json)
    {
        var doc = JsonSerializer.Deserialize<MachineProjectDocument>(json, Options)
                  ?? throw new ProjectDocumentLoadException(
                      ProjectDocumentLoadErrorCode.EmptyDocument,
                      "The project document is empty.");

        if (string.IsNullOrEmpty(doc.Schema))
        {
            doc.Schema = MachineProjectDocument.CurrentSchema;
        }

        if (!Version.TryParse(doc.Schema, out var schemaVersion)
            || schemaVersion > CurrentSchemaVersion)
        {
            throw new ProjectDocumentLoadException(
                ProjectDocumentLoadErrorCode.UnsupportedSchema,
                $"Unsupported machine project schema '{doc.Schema}'. " +
                $"The latest supported schema is '{MachineProjectDocument.CurrentSchema}'.",
                doc.Schema);
        }

        doc.Simulation ??= new SimulationDefinition();
        doc.Simulation.TestScenarioAssertions ??= new List<TestScenarioAssertionDefinition>();
        doc.Layouts ??= new List<Layouts.MachineLayoutDefinition>();
        doc.Axes ??= new List<Axes.VirtualAxisDefinition>();
        if (doc.MultiAxisCommissioningRecipe is not null)
        {
            doc.MultiAxisCommissioningRecipe.Targets ??= new List<MultiAxisCommissioningTargetDefinition>();
        }
        doc.Devices ??= new List<Devices.DeviceDefinition>();
        doc.Channels ??= new List<Channels.ChannelDefinition>();
        doc.Sequences ??= new List<Sequences.SequenceDefinition>();

        return doc;
    }

    public async Task SaveAsync(MachineProjectDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The project path must include a directory.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var modifiedAt = DateTimeOffset.UtcNow;
        var json = SerializeForSave(document, modifiedAt);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, fullPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }

            ApplySaveMetadata(document, modifiedAt);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<MachineProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        try
        {
            return await LoadFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryException) when (ShouldTryBackup(primaryException))
        {
            try
            {
                return await LoadFileAsync(fullPath + ".bak", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception backupException) when (IsExpectedLoadFailure(backupException))
            {
                if (backupException is ProjectDocumentLoadException
                    {
                        ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema
                    })
                {
                    ExceptionDispatchInfo.Capture(backupException).Throw();
                }

                ExceptionDispatchInfo.Capture(primaryException).Throw();
                throw;
            }
        }
    }

    private string SerializeForSave(MachineProjectDocument document, DateTimeOffset modifiedAt)
    {
        var root = JsonNode.Parse(Serialize(document))?.AsObject()
            ?? throw new InvalidOperationException("Failed to serialize project document.");
        root["schema"] = MachineProjectDocument.CurrentSchema;
        root["modifiedAt"] = modifiedAt;
        return root.ToJsonString(Options);
    }

    private static void ApplySaveMetadata(MachineProjectDocument document, DateTimeOffset modifiedAt)
    {
        document.Schema = MachineProjectDocument.CurrentSchema;
        document.ModifiedAt = modifiedAt;
    }

    private async Task<MachineProjectDocument> LoadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Load(json);
    }

    private static bool ShouldTryBackup(Exception exception) => exception switch
    {
        ProjectDocumentLoadException
        {
            ErrorCode: ProjectDocumentLoadErrorCode.UnsupportedSchema
        } => false,
        _ => IsExpectedLoadFailure(exception)
    };

    private static bool IsExpectedLoadFailure(Exception exception) => exception switch
    {
        ProjectDocumentLoadException => true,
        JsonException => true,
        IOException => true,
        UnauthorizedAccessException => true,
        ArgumentOutOfRangeException => true,
        _ => false
    };
}
