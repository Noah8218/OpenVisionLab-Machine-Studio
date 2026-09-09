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

    /// <summary>
    /// Creates the canonical on-disk JSON payload without mutating the document.
    /// The file adapter applies the returned timestamp only after its atomic commit succeeds.
    /// </summary>
    public string SerializeForSave(MachineProjectDocument document, DateTimeOffset modifiedAt)
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
}
