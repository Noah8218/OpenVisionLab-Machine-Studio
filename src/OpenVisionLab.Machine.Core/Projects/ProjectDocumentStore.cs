using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenVisionLab.Machine.Core.Projects;

public sealed class ProjectDocumentStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Save(MachineProjectDocument document)
    {
        document.Schema = MachineProjectDocument.CurrentSchema;
        document.ModifiedAt = DateTimeOffset.UtcNow;
        return Serialize(document);
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
                  ?? throw new InvalidOperationException("Failed to deserialize project document.");

        if (string.IsNullOrEmpty(doc.Schema))
        {
            doc.Schema = MachineProjectDocument.CurrentSchema;
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
        var json = Save(document);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The project path must include a directory.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

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
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Load(json);
    }
}
