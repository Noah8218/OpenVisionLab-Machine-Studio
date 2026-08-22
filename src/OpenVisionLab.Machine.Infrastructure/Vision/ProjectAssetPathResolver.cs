namespace OpenVisionLab.Machine.Infrastructure.Vision;

public sealed record ProjectAssetPath(string FullPath, string RelativePath);

/// <summary>
/// Resolves project-owned assets without allowing an authored relative path to
/// escape the explicitly supplied project root.
/// </summary>
public sealed class ProjectAssetPathResolver
{
    private readonly string _projectRootWithSeparator;

    public ProjectAssetPathResolver(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        ProjectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(ProjectRoot))
        {
            throw new DirectoryNotFoundException($"Project root was not found: '{ProjectRoot}'.");
        }

        _projectRootWithSeparator = Path.EndsInDirectorySeparator(ProjectRoot)
            ? ProjectRoot
            : ProjectRoot + Path.DirectorySeparatorChar;
    }

    public string ProjectRoot { get; }

    public ProjectAssetPath ResolveFile(string projectRelativePath)
    {
        if (string.IsNullOrWhiteSpace(projectRelativePath))
        {
            throw new ArgumentException("A project-relative asset path is required.", nameof(projectRelativePath));
        }

        if (Path.IsPathRooted(projectRelativePath) || Path.IsPathFullyQualified(projectRelativePath))
        {
            throw new ArgumentException("The asset path must be relative to the project root.", nameof(projectRelativePath));
        }

        var authoredSegments = projectRelativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        if (authoredSegments.Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The asset path cannot contain parent-directory traversal.", nameof(projectRelativePath));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(projectRelativePath, ProjectRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The asset path is not valid.", nameof(projectRelativePath), exception);
        }

        if (!fullPath.StartsWith(_projectRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The asset path resolves outside the project root.", nameof(projectRelativePath));
        }

        var normalizedRelativePath = Path.GetRelativePath(ProjectRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (normalizedRelativePath is "." or "" ||
            normalizedRelativePath.StartsWith("../", StringComparison.Ordinal))
        {
            throw new ArgumentException("The asset path must identify a file inside the project root.", nameof(projectRelativePath));
        }

        return new ProjectAssetPath(fullPath, normalizedRelativePath);
    }

    public ProjectAssetPath ResolveExistingFile(string projectRelativePath)
    {
        var asset = ResolveFile(projectRelativePath);
        if (!File.Exists(asset.FullPath))
        {
            throw new FileNotFoundException(
                $"Project asset was not found: '{asset.RelativePath}'.",
                asset.FullPath);
        }

        EnsureLinkedTargetsStayWithinProject(asset);
        return asset;
    }

    private void EnsureLinkedTargetsStayWithinProject(ProjectAssetPath asset)
    {
        var physicalProjectRoot = ResolveLinkTarget(new DirectoryInfo(ProjectRoot)) ?? ProjectRoot;
        var physicalRootWithSeparator = Path.EndsInDirectorySeparator(physicalProjectRoot)
            ? physicalProjectRoot
            : physicalProjectRoot + Path.DirectorySeparatorChar;
        var currentPath = ProjectRoot;
        var relativeSegments = asset.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < relativeSegments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, relativeSegments[index]);
            FileSystemInfo fileSystemInfo = index == relativeSegments.Length - 1
                ? new FileInfo(currentPath)
                : new DirectoryInfo(currentPath);

            var resolvedTarget = ResolveLinkTarget(fileSystemInfo);
            if (resolvedTarget is null)
            {
                continue;
            }

            var normalizedTarget = Path.GetFullPath(resolvedTarget);
            if (!normalizedTarget.StartsWith(physicalRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedTarget, physicalProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Project asset link resolves outside the project root: '{asset.RelativePath}'.");
            }
        }
    }

    private static string? ResolveLinkTarget(FileSystemInfo fileSystemInfo)
    {
        if (fileSystemInfo.LinkTarget is null)
        {
            return null;
        }

        return fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }
}
