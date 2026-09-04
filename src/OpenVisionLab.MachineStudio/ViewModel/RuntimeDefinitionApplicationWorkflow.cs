using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal enum RuntimeDefinitionApplicationOutcome
{
    Applied,
    CompilationRejected,
    EngineRejected
}

internal sealed record RuntimeDefinitionApplicationResult(
    RuntimeDefinitionApplicationOutcome Outcome,
    string? CompilationDetail,
    SimulationCommandResult? CommandResult)
{
    internal bool IsAccepted => Outcome == RuntimeDefinitionApplicationOutcome.Applied;
}

/// <summary>
/// Compiles an authored project and applies its validated runtime definition
/// to the live simulation engine. Project lifecycle and presentation remain in
/// MainViewModel.
/// </summary>
internal sealed class RuntimeDefinitionApplicationWorkflow
{
    private readonly ISimulationEngine _engine;
    private readonly MachineProjectRuntimeCompiler _compiler;

    internal RuntimeDefinitionApplicationWorkflow(
        ISimulationEngine engine,
        TimeSpan fixedStep)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _compiler = new MachineProjectRuntimeCompiler(fixedStep);
    }

    internal async Task<RuntimeDefinitionApplicationResult> ApplyAsync(
        MachineProjectDocument? project)
    {
        var compilation = _compiler.Compile(project);
        if (!compilation.IsSuccess)
        {
            return new(
                RuntimeDefinitionApplicationOutcome.CompilationRejected,
                FormatCompilationErrors(compilation),
                null);
        }

        var commandResult = await _engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(compilation.Configuration!));
        return commandResult.IsAccepted
            ? new(
                RuntimeDefinitionApplicationOutcome.Applied,
                null,
                commandResult)
            : new(
                RuntimeDefinitionApplicationOutcome.EngineRejected,
                null,
                commandResult);
    }

    private static string FormatCompilationErrors(
        MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(
            "; ",
            compilation.Errors.Select(error =>
                $"{error.Code}({error.TargetId ?? "project"}): {error.Message}"));
}
