namespace OpenVisionLab.Machine.Core.Projects;

public enum ProjectDocumentLoadSource
{
    Primary,
    Backup
}

public enum ProjectDocumentRecoveryReason
{
    PrimaryMissing,
    PrimaryUnreadable,
    PrimaryInvalid
}

public sealed record ProjectDocumentLoadResult(
    MachineProjectDocument Document,
    string ProjectPath,
    string SourcePath,
    ProjectDocumentLoadSource Source,
    ProjectDocumentRecoveryReason? RecoveryReason = null)
{
    public bool IsRecoveredFromBackup => Source == ProjectDocumentLoadSource.Backup;
}
