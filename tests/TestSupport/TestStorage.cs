namespace OpenVisionLab.TestSupport;

public static class TestStorage
{
    private const string DefaultRoot = @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio";
    private const long MinimumFreeSpaceBytes = 1_048_576;
    private static readonly Lazy<string> Root = new(ResolveRoot);

    public static string RootPath => Root.Value;

    private static string ResolveRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("OPENVISIONLAB_TEST_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot.Trim());
        }

        return HasEnoughFreeSpace(DefaultRoot)
            ? DefaultRoot
            : Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-Studio-TestData");
    }

    private static bool HasEnoughFreeSpace(string path)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrWhiteSpace(driveRoot)
                && new DriveInfo(driveRoot).AvailableFreeSpace >= MinimumFreeSpaceBytes;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }
}
