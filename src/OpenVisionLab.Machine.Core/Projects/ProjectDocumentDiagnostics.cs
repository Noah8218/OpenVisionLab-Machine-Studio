namespace OpenVisionLab.Machine.Core.Projects;

public enum ProjectDocumentDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public enum ProjectDocumentDiagnosticCode
{
    NoSavedPath,
    CurrentDocumentHasUnsavedChanges,
    PrimaryFileHealthy,
    PrimaryFileDiffersFromCurrent,
    PrimaryFileMissing,
    PrimaryFileUnreadable,
    PrimaryFileInvalid,
    PrimaryFileUnsupportedSchema,
    RecoveryAvailable,
    RecoveryUnavailable,
    BackupFileInvalid,
    BackupFileUnreadable,
    InspectionFailed
}

public sealed record ProjectDocumentDiagnosticItem(
    ProjectDocumentDiagnosticCode Code,
    ProjectDocumentDiagnosticSeverity Severity,
    string Detail,
    bool IsRepairable = false,
    string? RelatedPath = null);

public sealed record ProjectDocumentDiagnosticReport(
    string? ProjectPath,
    IReadOnlyList<ProjectDocumentDiagnosticItem> Items,
    string? RecoverySourcePath)
{
    public bool HasErrors => Items.Any(item =>
        item.Severity == ProjectDocumentDiagnosticSeverity.Error);

    public bool HasWarnings => Items.Any(item =>
        item.Severity == ProjectDocumentDiagnosticSeverity.Warning);

    public bool IsHealthy => !HasErrors && !HasWarnings;

    public bool CanPreviewRecovery => !string.IsNullOrWhiteSpace(RecoverySourcePath);
}

public sealed record ProjectDocumentRecoveryPreview(
    string ProjectPath,
    string SourcePath,
    string SourceHash,
    string ProjectId,
    string ProjectName,
    string ProjectSchema);
