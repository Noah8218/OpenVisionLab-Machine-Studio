using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProcessBlockContext
{
    public required MachineProjectDocument Project { get; init; }
    public required ProjectDocumentStore Store { get; init; }
    public required string ProjectBefore { get; init; }
    public required SimulationSnapshot? RuntimeBefore { get; init; }
    public required Border Panel { get; init; }
    public required Button ApplyButton { get; init; }
    public required CheckBox LoadBlockCheckBox { get; init; }
}

internal sealed class SmokeAppliedProcessBlockContext
{
    public required SmokeProcessBlockContext PreviewContext { get; init; }
    public required string ProjectAfterApply { get; init; }
}

internal static class SmokeProcessBlockPreparation
{
    public static async Task<SmokeProcessBlockContext> PrepareAsync(
        ShellWindow window,
        MainViewModel vm,
        MachineProjectDocument? initialProject,
        RecipeConnectionWorkbenchView workbench,
        Func<DependencyObject, Func<Border, bool>, Border?> findBorder,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Func<DependencyObject, Func<CheckBox, bool>, CheckBox?> findCheckBox)
    {
        var project = initialProject
            ?? throw new InvalidOperationException("A project is required for process-block smoke.");
        var store = new ProjectDocumentStore();
        var projectBefore = store.SerializeForEvidence(project);
        var runtimeBefore = vm.SceneSnapshots.Latest;
        vm.RecipeConnections.ProcessBlocks.PreviewProcessBlockCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var panel = findBorder(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "SemiconductorProcessBlockPreview",
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Process block preview was not available.");
        var applyButton = findButton(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ApplySemiconductorProcessBlockButton",
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Process block Apply button was not available.");
        var loadBlockCheckBox = findCheckBox(
                workbench,
                candidate => string.Equals(
                    candidate.Name,
                    "ProcessBlockLoadCheckBox",
                    StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Load block checkbox was not available.");

        Check(
            panel.IsVisible
            && vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockCount == 5
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Count == 13
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.All(item => item.IsProposed)
            && loadBlockCheckBox.IsChecked == true
            && applyButton.IsEnabled,
            "The five-block plan did not preview its thirteen proposed steps.");
        Check(
            projectBefore == store.SerializeForEvidence(project)
            && runtimeBefore?.TickIndex == vm.SceneSnapshots.Latest?.TickIndex
            && runtimeBefore?.SimulationTime == vm.SceneSnapshots.Latest?.SimulationTime
            && vm.IsDesignMode
            && !vm.IsRunning,
            "Process block preview changed the project or runtime.");

        return new SmokeProcessBlockContext
        {
            Project = project,
            Store = store,
            ProjectBefore = projectBefore,
            RuntimeBefore = runtimeBefore,
            Panel = panel,
            ApplyButton = applyButton,
            LoadBlockCheckBox = loadBlockCheckBox
        };
    }

    public static async Task<SmokeAppliedProcessBlockContext> ApplyAndRecognizeAsync(
        ShellWindow window,
        MainViewModel vm,
        SmokeProcessBlockContext context)
    {
        vm.RecipeConnections.ProcessBlocks.ApplyProcessBlockCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            vm.RecipeConnections.RecipeStepCount == 25
            && vm.IsDesignMode
            && !vm.IsRunning,
            "The editable plan setup did not create the stopped 25-step recipe.");

        var projectAfterApply = context.Store.SerializeForEvidence(context.Project);
        vm.RecipeConnections.ProcessBlocks.PreviewProcessBlockCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(
            vm.RecipeConnections.ProcessBlocks.SelectedProcessBlockCount == 5
            && vm.RecipeConnections.ProcessBlocks.ExistingProcessBlockCount == 5
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.Count == 13
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.All(item => item.IsAlreadyConfigured)
            && !context.ApplyButton.IsEnabled
            && projectAfterApply == context.Store.SerializeForEvidence(context.Project),
            "The applied five-block plan was not recognized without mutation.");

        var sequence = context.Project.Sequences.FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            context.Project.Simulation.AutomaticRun?.SequenceId,
            StringComparison.Ordinal));
        Check(
            sequence is not null
            && vm.RecipeConnections.ProcessBlocks.ProcessBlockItems.All(item =>
            {
                var step = sequence!.Steps.FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    item.StepId,
                    StringComparison.Ordinal));
                if (step is null)
                {
                    return false;
                }

                var valueText = string.IsNullOrWhiteSpace(step.Parameter) ? "—" : step.Parameter;
                return item.DetailText.Contains(
                        $"{OpenVisionLanguageService.T("Sequence.Target")}: {step.TargetId}",
                        StringComparison.Ordinal)
                    && item.DetailText.Contains(
                        $"{OpenVisionLanguageService.T("Sequence.Value")}: {valueText}",
                        StringComparison.Ordinal)
                    && item.DetailText.Contains(
                        $"{OpenVisionLanguageService.T("Sequence.Timeout")}: "
                        + $"{step.TimeoutMs.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} ms",
                        StringComparison.Ordinal);
            })
            && projectAfterApply == context.Store.SerializeForEvidence(context.Project),
            "Existing process cards did not show their exact current target, value, and timeout safely.");

        return new SmokeAppliedProcessBlockContext
        {
            PreviewContext = context,
            ProjectAfterApply = projectAfterApply
        };
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
