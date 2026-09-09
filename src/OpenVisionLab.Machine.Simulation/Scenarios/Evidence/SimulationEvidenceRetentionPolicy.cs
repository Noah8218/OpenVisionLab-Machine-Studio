using System.Collections.Immutable;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Identifies how an evidence file grows over its lifetime. Fixed sidecars are
/// overwritten by the owning project, while append-only exports accumulate
/// files and can participate in age, count, and byte retention.
/// </summary>
public enum SimulationEvidenceArtifactStorageMode
{
    FixedSidecar,
    AppendOnlyExport
}

/// <summary>
/// File metadata captured by the retention owner. Current references and an
/// accepted baseline are always protected from deletion.
/// </summary>
public sealed record SimulationEvidenceArtifactDescriptor(
    string Path,
    SimulationEvidenceArtifactStorageMode StorageMode,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    bool IsCurrentReference = false,
    bool IsAcceptedBaseline = false)
{
    public bool IsProtected => IsCurrentReference || IsAcceptedBaseline;
}

/// <summary>
/// Explicit limits for append-only evidence. Zero disables an individual
/// limit; fixed sidecars are retained because their owner overwrites them.
/// </summary>
public sealed record SimulationEvidenceRetentionPolicy(
    TimeSpan MaximumAppendOnlyExportAge = default,
    long MaximumAppendOnlyExportBytes = 0,
    int MaximumAppendOnlyExportCount = 0,
    long MinimumFreeBytes = 0)
{
    internal void Validate()
    {
        if (MaximumAppendOnlyExportAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAppendOnlyExportAge));
        }

        if (MaximumAppendOnlyExportBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAppendOnlyExportBytes));
        }

        if (MaximumAppendOnlyExportCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAppendOnlyExportCount));
        }

        if (MinimumFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeBytes));
        }
    }
}

/// <summary>
/// Deterministic retention decision for one captured evidence index. The plan
/// never mutates the file system; callers must explicitly apply it.
/// </summary>
public sealed record SimulationEvidenceRetentionPlan(
    SimulationEvidenceRetentionPolicy Policy,
    ImmutableArray<SimulationEvidenceArtifactDescriptor> RetainedArtifacts,
    ImmutableArray<SimulationEvidenceArtifactDescriptor> DeletionCandidates,
    long TotalBytes,
    long RetainedBytes,
    long AvailableFreeBytes,
    string? UnsatisfiedConstraint)
{
    public bool IsSatisfiable => UnsatisfiedConstraint is null;

    public long BytesToRelease
    {
        get
        {
            long total = 0;
            foreach (var artifact in DeletionCandidates)
            {
                if (artifact.SizeBytes < 0 || artifact.SizeBytes > long.MaxValue - total)
                {
                    throw new InvalidOperationException("Retention plan byte count is invalid.");
                }

                total += artifact.SizeBytes;
            }

            return total;
        }
    }
}

/// <summary>
/// Applies an already reviewed retention plan after a fresh preflight. It is
/// intentionally explicit so project save, export, and UI confirmation can
/// choose when deletion is appropriate.
/// </summary>
public static class SimulationEvidenceRetentionExecutor
{
    public static SimulationEvidenceRetentionExecutionResult Apply(
        SimulationEvidenceRetentionPlan plan,
        Func<string, long>? getAvailableFreeBytes = null,
        Func<string, bool>? isCurrentReference = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        getAvailableFreeBytes ??= GetAvailableFreeBytes;
        isCurrentReference ??= static _ => false;

        if (!plan.IsSatisfiable)
        {
            return Failure(plan.UnsatisfiedConstraint!);
        }

        var candidates = plan.DeletionCandidates;
        if (candidates.Any(item => item.StorageMode != SimulationEvidenceArtifactStorageMode.AppendOnlyExport
            || item.IsProtected))
        {
            return Failure("The retention plan contains a protected or fixed-sidecar deletion candidate.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate.Path);
                if (!paths.Add(fullPath))
                {
                    return Failure("The retention plan contains duplicate deletion paths.");
                }

                if (isCurrentReference(fullPath))
                {
                    return Failure($"The evidence path is referenced by the current state: {fullPath}");
                }

                if (!File.Exists(fullPath))
                {
                    return Failure($"The evidence path is missing: {fullPath}");
                }

                if (new FileInfo(fullPath).Length != candidate.SizeBytes)
                {
                    return Failure($"The evidence path changed since planning: {fullPath}");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failure(exception.Message);
        }

        if (plan.Policy.MinimumFreeBytes > 0 && candidates.Length > 0)
        {
            long availableFreeBytes;
            try
            {
                availableFreeBytes = getAvailableFreeBytes(candidates[0].Path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Failure(exception.Message);
            }
            if (availableFreeBytes < 0)
            {
                return Failure("The target drive free-space measurement was invalid.");
            }

            if (plan.BytesToRelease > long.MaxValue - availableFreeBytes
                || availableFreeBytes + plan.BytesToRelease < plan.Policy.MinimumFreeBytes)
            {
                return Failure("The planned deletion cannot satisfy the configured minimum free-space policy.");
            }
        }

        var deletedPaths = ImmutableArray.CreateBuilder<string>(candidates.Length);
        try
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate.Path);
                File.Delete(fullPath);
                deletedPaths.Add(fullPath);
            }

            return new(true, deletedPaths.ToImmutable(), null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, deletedPaths.ToImmutable(), exception.Message);
        }
    }

    private static long GetAvailableFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
        {
            return -1;
        }

        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static SimulationEvidenceRetentionExecutionResult Failure(string reason) =>
        new(false, ImmutableArray<string>.Empty, reason);
}

public sealed record SimulationEvidenceRetentionExecutionResult(
    bool IsComplete,
    ImmutableArray<string> DeletedPaths,
    string? FailureReason);

/// <summary>
/// Produces a deterministic, fail-closed retention plan from a captured file
/// index. Oldest append-only exports are selected first; protected references
/// and fixed sidecars stay in the retained set.
/// </summary>
public static class SimulationEvidenceRetentionPlanner
{
    public static SimulationEvidenceRetentionPlan Plan(
        IEnumerable<SimulationEvidenceArtifactDescriptor> artifacts,
        SimulationEvidenceRetentionPolicy policy,
        DateTimeOffset now,
        long availableFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (availableFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableFreeBytes));
        }

        var normalized = NormalizeArtifacts(artifacts);
        var ordered = normalized
            .OrderBy(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        var eligible = ordered
            .Where(item => item.StorageMode == SimulationEvidenceArtifactStorageMode.AppendOnlyExport
                && !item.IsProtected)
            .ToArray();
        var deletions = new Dictionary<string, SimulationEvidenceArtifactDescriptor>(StringComparer.OrdinalIgnoreCase);

        void MarkForDeletion(SimulationEvidenceArtifactDescriptor artifact) =>
            deletions.TryAdd(artifact.Path, artifact);

        if (policy.MaximumAppendOnlyExportAge > TimeSpan.Zero)
        {
            foreach (var artifact in eligible)
            {
                if (IsOlderThan(artifact.LastWriteTimeUtc, now, policy.MaximumAppendOnlyExportAge))
                {
                    MarkForDeletion(artifact);
                }
            }
        }

        var appendOnlyBytes = SumBytes(ordered
            .Where(item => IsAppendOnlyExport(item) && !deletions.ContainsKey(item.Path)));
        var appendOnlyCount = ordered.Count(
            item => IsAppendOnlyExport(item) && !deletions.ContainsKey(item.Path));
        foreach (var artifact in eligible)
        {
            if (policy.MaximumAppendOnlyExportBytes > 0
                && appendOnlyBytes > policy.MaximumAppendOnlyExportBytes
                && !deletions.ContainsKey(artifact.Path))
            {
                MarkForDeletion(artifact);
                appendOnlyBytes -= artifact.SizeBytes;
                appendOnlyCount--;
            }

            if (policy.MaximumAppendOnlyExportCount > 0
                && appendOnlyCount > policy.MaximumAppendOnlyExportCount
                && !deletions.ContainsKey(artifact.Path))
            {
                MarkForDeletion(artifact);
                appendOnlyBytes -= artifact.SizeBytes;
                appendOnlyCount--;
            }
        }

        var bytesToRelease = SumBytes(deletions.Values);
        if (policy.MinimumFreeBytes > 0
            && availableFreeBytes < policy.MinimumFreeBytes)
        {
            foreach (var artifact in eligible)
            {
                if (availableFreeBytes > long.MaxValue - bytesToRelease
                    || availableFreeBytes + bytesToRelease >= policy.MinimumFreeBytes)
                {
                    break;
                }

                if (deletions.ContainsKey(artifact.Path))
                {
                    continue;
                }

                MarkForDeletion(artifact);
                bytesToRelease += artifact.SizeBytes;
            }
        }

        var deletionCandidates = ordered
            .Where(item => deletions.ContainsKey(item.Path))
            .ToImmutableArray();
        var retainedArtifacts = ordered
            .Where(item => !deletions.ContainsKey(item.Path))
            .ToImmutableArray();
        var retainedBytes = SumBytes(retainedArtifacts);
        var retainedAppendOnlyBytes = SumBytes(retainedArtifacts.Where(IsAppendOnlyExport));
        var retainedAppendOnlyCount = retainedArtifacts.Count(IsAppendOnlyExport);
        var unsatisfied = new List<string>();
        if (policy.MaximumAppendOnlyExportBytes > 0
            && retainedAppendOnlyBytes > policy.MaximumAppendOnlyExportBytes)
        {
            unsatisfied.Add("maximum append-only export bytes");
        }

        if (policy.MaximumAppendOnlyExportCount > 0
            && retainedAppendOnlyCount > policy.MaximumAppendOnlyExportCount)
        {
            unsatisfied.Add("maximum append-only export count");
        }

        if (policy.MinimumFreeBytes > 0
            && (bytesToRelease > long.MaxValue - availableFreeBytes
                || availableFreeBytes + bytesToRelease < policy.MinimumFreeBytes))
        {
            unsatisfied.Add("minimum free space");
        }

        return new(
            policy,
            retainedArtifacts,
            deletionCandidates,
            SumBytes(ordered),
            retainedBytes,
            availableFreeBytes,
            unsatisfied.Count == 0 ? null : string.Join(", ", unsatisfied));
    }

    private static ImmutableArray<SimulationEvidenceArtifactDescriptor> NormalizeArtifacts(
        IEnumerable<SimulationEvidenceArtifactDescriptor> artifacts)
    {
        var normalized = ImmutableArray.CreateBuilder<SimulationEvidenceArtifactDescriptor>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.Path))
            {
                throw new InvalidOperationException("Retention index contains an empty evidence path.");
            }

            if (artifact.SizeBytes < 0)
            {
                throw new InvalidOperationException("Retention index contains a negative file size.");
            }

            if (!Enum.IsDefined(artifact.StorageMode))
            {
                throw new InvalidOperationException("Retention index contains an unknown storage mode.");
            }

            var fullPath = Path.GetFullPath(artifact.Path);
            if (!paths.Add(fullPath))
            {
                throw new InvalidOperationException(
                    $"Retention index contains duplicate evidence paths: {fullPath}");
            }

            normalized.Add(artifact with
            {
                Path = fullPath,
                LastWriteTimeUtc = artifact.LastWriteTimeUtc.ToUniversalTime()
            });
        }

        return normalized.ToImmutable();
    }

    private static bool IsAppendOnlyExport(SimulationEvidenceArtifactDescriptor artifact) =>
        artifact.StorageMode == SimulationEvidenceArtifactStorageMode.AppendOnlyExport;

    private static bool IsOlderThan(
        DateTimeOffset lastWriteTimeUtc,
        DateTimeOffset now,
        TimeSpan maximumAge) =>
        lastWriteTimeUtc <= now && now - lastWriteTimeUtc > maximumAge;

    private static long SumBytes(IEnumerable<SimulationEvidenceArtifactDescriptor> artifacts)
    {
        long total = 0;
        foreach (var artifact in artifacts)
        {
            if (artifact.SizeBytes > long.MaxValue - total)
            {
                throw new InvalidOperationException("Retention index byte count overflowed.");
            }

            total += artifact.SizeBytes;
        }

        return total;
    }
}
