using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class DirectExeSmokeProjectLoaderTests
{
    [Fact]
    public void CameraFirstUseStartsWithUntitledProject()
    {
        var result = DirectExeSmokeProjectLoader.Load(
            projectPath: null,
            cameraFirstUseRequested: true,
            startupChoiceState: null);

        Assert.NotNull(result.Project);
        Assert.Equal("Untitled", result.Project!.Name);
        Assert.Null(result.InitialProjectPath);
        Assert.Null(result.StartupSamplePath);
    }

    [Fact]
    public void StartupChoicePreservesSamplePathWithoutLoadingProject()
    {
        var result = DirectExeSmokeProjectLoader.Load(
            projectPath: null,
            cameraFirstUseRequested: false,
            startupChoiceState: "sample-hover");

        Assert.Null(result.Project);
        Assert.Null(result.InitialProjectPath);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Samples", "AutomaticTransferCell.ovmachine"),
            result.StartupSamplePath);
    }

    [Fact]
    public void ExistingProjectPathLoadsProjectAndRetainsPath()
    {
        var samplePath = Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "AutomaticTransferCell.ovmachine");

        var result = DirectExeSmokeProjectLoader.Load(
            samplePath,
            cameraFirstUseRequested: false,
            startupChoiceState: null);

        Assert.NotNull(result.Project);
        Assert.Equal(Path.GetFullPath(samplePath), Path.GetFullPath(result.InitialProjectPath!));
        Assert.Equal("Automatic Transfer Cell", result.Project!.Name);
        Assert.Null(result.StartupSamplePath);
    }

    [Fact]
    public void MissingExplicitProjectPathDoesNotFallbackToBundledSample()
    {
        var result = DirectExeSmokeProjectLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "missing-project.ovmachine"),
            cameraFirstUseRequested: false,
            startupChoiceState: null);

        Assert.Null(result.Project);
        Assert.Null(result.InitialProjectPath);
        Assert.Null(result.StartupSamplePath);
    }
}
