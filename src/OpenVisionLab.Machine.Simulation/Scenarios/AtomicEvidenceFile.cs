namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Owns temporary-file lifetime and replacement for Simulation evidence.
/// Callers retain validation, path preparation, serialization, and encoding.
/// </summary>
internal static class AtomicEvidenceFile
{
    internal static void Write(string fullPath, Action<string> writeTemporaryFile)
    {
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // The synchronous writer must finish and close its file before replacement.
            writeTemporaryFile(temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
