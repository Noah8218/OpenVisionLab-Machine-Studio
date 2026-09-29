using System.Reflection;
using System.Text;

namespace OpenVisionLab;

public enum OpenVisionLanguage
{
    Korean,
    English
}

public sealed class OpenVisionLanguageOption
{
    public OpenVisionLanguageOption(OpenVisionLanguage language, string displayName)
    {
        Language = language;
        DisplayName = displayName;
    }

    public OpenVisionLanguage Language { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

public static class OpenVisionLanguageService
{
    private const string ConfigDirectoryName = "CONFIG";
    private const string CompanyDirectoryName = "OpenVisionLab";
    private const string ProductDirectoryName = "MachineStudio";
    private const string CatalogFileName = "localization_catalog.tsv";
    private const string LanguageFileName = "language.txt";
    private static readonly Dictionary<string, (string Korean, string English)> LegacyWorkspaceDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Equipment.OutlineNoMatches"] = ("일치하는 부품이 없습니다.", "No matching parts"),
            ["Equipment.OutlineSearch"] = ("이름 · ID · 종류 · 유닛 검색", "Search name · ID · kind · unit"),
            ["Workspace.Teaching"] = ("동작·검사 티칭", "Motion & inspection teaching"),
            ["Workspace.Execution"] = ("실행·검증", "Run & validate"),
            ["Workspace.Results"] = ("결과·비교", "Results & comparison")
        };
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, OpenVisionLocalizationEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static bool loaded;

    public static event EventHandler? LanguageChanged;

    public static OpenVisionLanguage CurrentLanguage { get; private set; } = OpenVisionLanguage.Korean;

    public static IReadOnlyList<OpenVisionLanguageOption> LanguageOptions { get; } =
    [
        new(OpenVisionLanguage.Korean, "한국어"),
        new(OpenVisionLanguage.English, "English")
    ];

    public static string CatalogPath => Path.Combine(GetConfigDirectory(), CatalogFileName);

    public static void Load()
    {
        lock (SyncRoot)
        {
            if (loaded)
            {
                return;
            }

            EnsureCatalogFile();
            LoadCatalog();
            LoadLanguage();
            loaded = true;
        }
    }

    public static void SetLanguage(OpenVisionLanguage language, bool save = true)
    {
        EnsureLoaded();
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        if (save)
        {
            SaveLanguage(language);
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) => T(key, key, key);

    public static string T(string key, string korean, string english)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        EnsureLoaded();
        lock (SyncRoot)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                var localized = CurrentLanguage == OpenVisionLanguage.English
                    ? entry.English
                    : entry.Korean;
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    return localized;
                }
            }

            return CurrentLanguage == OpenVisionLanguage.English
                ? english
                : korean;
        }
    }

    /// <summary>
    /// Resolves project-authored text by stable domain/id while preserving the
    /// authored source text when no catalog entry exists.
    /// </summary>
    public static string TUserText(string scope, string id, string fallback)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(id))
        {
            return fallback ?? string.Empty;
        }

        string source = fallback ?? string.Empty;
        return T($"UserText.{scope}.{id}", source, source);
    }

    private static void EnsureLoaded()
    {
        if (!loaded)
        {
            Load();
        }
    }

    private static void EnsureCatalogFile() => EnsureCatalogFile(CatalogPath, ReadEmbeddedCatalog());

    internal static void EnsureCatalogFile(string catalogPath, string embeddedCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(embeddedCatalog);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!);
        var defaults = ParseCatalog(embeddedCatalog)
            .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(catalogPath))
        {
            File.WriteAllText(catalogPath, BuildCatalog(defaults.Values), Encoding.UTF8);
            return;
        }

        var current = ParseCatalog(File.ReadAllText(catalogPath, Encoding.UTF8))
            .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var entry in defaults.Values)
        {
            if (current.TryGetValue(entry.Key, out var existing))
            {
                if (LegacyWorkspaceDefaults.TryGetValue(entry.Key, out var previousDefault)
                    && existing.Korean == previousDefault.Korean
                    && existing.English == previousDefault.English)
                {
                    current[entry.Key] = entry;
                    changed = true;
                }
                continue;
            }

            current[entry.Key] = entry;
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(catalogPath, BuildCatalog(current.Values), Encoding.UTF8);
        }
    }

    private static string BuildCatalog(IEnumerable<OpenVisionLocalizationEntry> entries)
    {
        var builder = new StringBuilder("Key\tKorean\tEnglish\r\n");
        foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append(entry.Key)
                .Append('\t')
                .Append(entry.Korean)
                .Append('\t')
                .Append(entry.English)
                .Append("\r\n");
        }

        return builder.ToString();
    }

    private static void LoadCatalog()
    {
        var catalog = File.Exists(CatalogPath)
            ? File.ReadAllText(CatalogPath, Encoding.UTF8)
            : ReadEmbeddedCatalog();

        Entries.Clear();
        foreach (var entry in ParseCatalog(catalog))
        {
            Entries[entry.Key] = entry;
        }
    }

    private static IEnumerable<OpenVisionLocalizationEntry> ParseCatalog(string catalog)
    {
        foreach (var line in catalog.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            yield return new OpenVisionLocalizationEntry(
                parts[0].Trim(),
                parts.Length > 1 ? parts[1].Trim() : string.Empty,
                parts.Length > 2 ? parts[2].Trim() : string.Empty);
        }
    }

    private static string ReadEmbeddedCatalog()
    {
        var resourceName = typeof(OpenVisionLanguageService).Assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("LocalizationCatalog.tsv", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return "Key\tKorean\tEnglish\r\n";
        }

        using var stream = typeof(OpenVisionLanguageService).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Localization catalog resource was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void LoadLanguage()
    {
        CurrentLanguage = OpenVisionLanguage.Korean;
        try
        {
            var path = Path.Combine(GetConfigDirectory(), LanguageFileName);
            if (!File.Exists(path))
            {
                return;
            }

            var value = File.ReadAllText(path).Trim();
            CurrentLanguage = value.Equals("en", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("english", StringComparison.OrdinalIgnoreCase)
                ? OpenVisionLanguage.English
                : OpenVisionLanguage.Korean;
        }
        catch
        {
            CurrentLanguage = OpenVisionLanguage.Korean;
        }
    }

    private static void SaveLanguage(OpenVisionLanguage language)
    {
        try
        {
            Directory.CreateDirectory(GetConfigDirectory());
            File.WriteAllText(
                Path.Combine(GetConfigDirectory(), LanguageFileName),
                language == OpenVisionLanguage.English ? "en" : "ko",
                Encoding.UTF8);
        }
        catch
        {
            // Language changes remain active for the current process when persistence is unavailable.
        }
    }

    private static string GetConfigDirectory()
    {
        var smokeConfigDirectory = Environment.GetEnvironmentVariable(
            "OPENVISIONLAB_MACHINE_STUDIO_SMOKE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(smokeConfigDirectory)
            ? Path.GetFullPath(smokeConfigDirectory)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                CompanyDirectoryName,
                ProductDirectoryName,
                ConfigDirectoryName);
    }

    private sealed record OpenVisionLocalizationEntry(string Key, string Korean, string English);
}
