using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Compilation;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class PrealignerCompilerTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void Recipe03_CompilesTypedPrealignerAgainstExistingEquipment()
    {
        MachineProjectDocument project = LoadRecipe();

        MachineProjectRuntimeCompilationResult result =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);

        Assert.True(result.IsSuccess, ErrorSummary(result));
        var prealigner = Assert.Single(result.Configuration!.Layout!.Prealigners);
        Assert.Equal("alignment-table", prealigner.RotaryStageComponentId);
        Assert.Equal("axis.alignment-rotation", prealigner.RotaryAxisId);
        Assert.Equal("process-cylinder", prealigner.ClampCylinderComponentId);
        Assert.Equal(180, prealigner.AlignmentTargetDegrees);
        Assert.Equal(0.1, prealigner.AlignmentToleranceDegrees);
    }

    [Fact]
    public void WrongStageChannelKindAndOutOfRangeTarget_FailClosed()
    {
        MachineProjectDocument project = LoadRecipe();
        PrealignerDefinition definition = Assert.Single(project.Devices, device =>
            device.Kind == DeviceKind.Prealigner).Prealigner!;

        definition.RotaryStageComponentId = "process-stage";
        AssertPrealignerError(project);

        project = LoadRecipe();
        definition = Assert.Single(project.Devices, device => device.Kind == DeviceKind.Prealigner).Prealigner!;
        definition.AlignmentAcceptedCommandChannelId = "di.sensor-process";
        AssertPrealignerError(project);

        project = LoadRecipe();
        definition = Assert.Single(project.Devices, device => device.Kind == DeviceKind.Prealigner).Prealigner!;
        definition.AlignmentTargetDegrees = 400;
        AssertPrealignerError(project);
    }

    private static void AssertPrealignerError(MachineProjectDocument project)
    {
        MachineProjectRuntimeCompilationResult result =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(project);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error =>
            error.Code == MachineProjectRuntimeCompilationErrorCode.PrealignerConfigurationInvalid);
    }

    private static MachineProjectDocument LoadRecipe()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "SemiconductorRecipes",
            "03-WaferPrealigner.ovmachine");
        return new ProjectDocumentStore().Load(File.ReadAllText(path));
    }

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult result) =>
        string.Join(Environment.NewLine, result.Errors.Select(error =>
            $"{error.Code} [{error.TargetId}]: {error.Message}"));
}
