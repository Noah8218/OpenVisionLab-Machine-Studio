namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record ProjectDocumentSaveReceipt(
    string SessionId,
    long Revision,
    string SavedPath,
    string ContentHash);
