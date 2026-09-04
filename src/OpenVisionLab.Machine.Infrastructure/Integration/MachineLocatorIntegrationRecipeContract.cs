using System.Text.Json;

namespace OpenVisionLab.Machine.Infrastructure.Integration;

/// <summary>
/// Reads the producer-side portion of the locator integration recipe. The
/// recipe remains a consumer-owned JSON contract; Machine Studio only
/// resolves and stages the declared template as a transaction artifact.
/// </summary>
internal static class MachineLocatorIntegrationRecipeContract
{
    public const string SchemaVersion = "locator-relative-blob-integration-recipe-v1";
    public const string TemplateArtifactRole = "locator-template";
    public const string TemplateArtifactId = "locator-template";

    public static string? ResolveTemplatePath(string recipePath)
    {
        var fullRecipePath = Path.GetFullPath(recipePath);
        if (!string.Equals(
                Path.GetExtension(fullRecipePath),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var document = ParseRecipe(fullRecipePath);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.String
            || !string.Equals(schema.GetString(), SchemaVersion, StringComparison.Ordinal))
        {
            return null;
        }

        var artifactId = ReadRequiredString(root, "templateArtifactId", recipePath);
        if (!string.Equals(artifactId, TemplateArtifactId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Locator integration recipes must declare templateArtifactId '{TemplateArtifactId}'.",
                nameof(recipePath));
        }

        var declaredPath = ReadRequiredString(root, "templatePath", recipePath);
        var recipeDirectory = Path.GetDirectoryName(fullRecipePath)
            ?? throw new ArgumentException(
                "The locator integration recipe path has no parent directory.",
                nameof(recipePath));
        var templatePath = Path.GetFullPath(
            Path.IsPathRooted(declaredPath)
                ? declaredPath
                : Path.Combine(recipeDirectory, declaredPath));
        if (!File.Exists(templatePath)
            || new FileInfo(templatePath).Length <= 0)
        {
            throw new ArgumentException(
                $"The locator template declared by the integration recipe was not found: {templatePath}",
                nameof(recipePath));
        }

        return templatePath;
    }

    private static JsonDocument ParseRecipe(string recipePath)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(recipePath));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The locator integration recipe is not valid JSON.",
                nameof(recipePath),
                exception);
        }
    }

    private static string ReadRequiredString(
        JsonElement root,
        string propertyName,
        string recipePath)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ArgumentException(
                $"The locator integration recipe must declare a non-empty '{propertyName}'.",
                nameof(recipePath));
        }

        return property.GetString()!.Trim();
    }
}
