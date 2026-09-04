namespace OpenVisionLab.Machine.Core.Projects;

public enum ProjectDocumentLoadErrorCode
{
    EmptyDocument,
    UnsupportedSchema
}

public sealed class ProjectDocumentLoadException : Exception
{
    internal ProjectDocumentLoadException(
        ProjectDocumentLoadErrorCode errorCode,
        string message,
        string? projectSchema = null)
        : base(message)
    {
        ErrorCode = errorCode;
        ProjectSchema = projectSchema;
    }

    public ProjectDocumentLoadErrorCode ErrorCode { get; }
    public string? ProjectSchema { get; }
}
