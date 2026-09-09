using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeMultiAxisCommissioningStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    public static async Task ApplyStateAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(window)
            ?? throw new InvalidOperationException("Right inspector was unavailable.");
        switch (state.ToLowerInvariant())
        {
            case "design":
            case "design-focus":
            case "design-popup":
                if (!viewModel.IsDesignMode)
                {
                    throw new InvalidOperationException("Recipe design state requires Design mode.");
                }
                viewModel.ProjectTree.SelectedNode = viewModel.ProjectTree.Roots.Single();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                inspector.MultiAxisRecipeDesignPanel.BringIntoView();
                if (state.Equals("design-focus", StringComparison.OrdinalIgnoreCase))
                {
                    inspector.MultiAxisRecipeNameTextBox.Text = "Pick position smoke";
                    inspector.MultiAxisRecipeNameTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    interaction.ActivateWindow();
                    inspector.MultiAxisRecipeNameTextBox.Focus();
                    Keyboard.Focus(inspector.MultiAxisRecipeNameTextBox);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.MultiAxisRecipeNameTextBox.IsKeyboardFocusWithin
                        || inspector.MultiAxisRecipeNameTextBox.Text != "Pick position smoke")
                    {
                        throw new InvalidOperationException("Recipe name did not render its focused non-empty value.");
                    }
                }
                else if (state.Equals("design-popup", StringComparison.OrdinalIgnoreCase))
                {
                    var comboBox = SmokeVisualTreeQuery.FindVisualDescendant<ComboBox>(
                        inspector.MultiAxisRecipeDesignPanel,
                        candidate => candidate.IsVisible && candidate.IsEnabled)
                        ?? throw new InvalidOperationException("Recipe axis selector was unavailable.");
                    interaction.ActivateWindow();
                    comboBox.Focus();
                    comboBox.IsDropDownOpen = true;
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!comboBox.IsDropDownOpen)
                    {
                        throw new InvalidOperationException("Recipe axis selector popup did not open.");
                    }
                }
                break;
            case "ready":
            case "run-hover":
            case "run-pressed":
            case "validation-focus":
            case "validation-hover":
            case "validation-pressed":
            case "validated":
            case "validating":
            case "history-selected":
            case "baseline-pressed":
            case "baseline-accepted":
            case "baseline-mismatch":
            case "baseline-mismatch-x":
                viewModel.IsRunMode = true;
                inspector.RunInspectorScrollViewer.ScrollToTop();
                inspector.MultiAxisRecipeRunPanel.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!viewModel.RunMultiAxisCommissioningRecipeCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("Recipe Run was unavailable in its ready state.");
                }
                if (state.Equals("validation-focus", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions = 4;
                    interaction.ActivateWindow();
                    inspector.CommissioningValidationRepetitionsTextBox.Focus();
                    Keyboard.Focus(inspector.CommissioningValidationRepetitionsTextBox);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.CommissioningValidationRepetitionsTextBox.IsKeyboardFocusWithin
                        || inspector.CommissioningValidationRepetitionsTextBox.Text != "4")
                    {
                        throw new InvalidOperationException("Commissioning repetitions did not render its focused value.");
                    }
                }
                else if (state.Equals("validation-hover", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("validation-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.ActivateWindow();
                    interaction.MovePointerToCenter(inspector.ValidateMultiAxisRecipeButton);
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.ValidateMultiAxisRecipeButton.IsMouseOver)
                    {
                        throw new InvalidOperationException("Recipe validation did not enter pointer-hover state.");
                    }
                    if (state.Equals("validation-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        interaction.MarkSmokePointerHeld();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!inspector.ValidateMultiAxisRecipeButton.IsPressed)
                        {
                            throw new InvalidOperationException("Recipe validation did not enter pointer-down state.");
                        }
                    }
                }
                else if (state.Equals("history-selected", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-pressed", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-accepted", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions = 2;
                    var initialHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
                    viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                    for (var attempt = 0; attempt < 200
                         && (viewModel.IsCommissioningValidationRunning
                             || viewModel.CommissioningResultHistory.Entries.Length <= initialHistoryCount);
                         attempt++)
                    {
                        await Task.Delay(25);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    viewModel.SelectedCommissioningHistoryEntry =
                        viewModel.CommissioningResultHistory.Entries.LastOrDefault();
                    if (viewModel.SelectedCommissioningHistoryEntry is null)
                    {
                        throw new InvalidOperationException("Commissioning history selection was unavailable.");
                    }

                    if (!state.Equals("history-selected", StringComparison.OrdinalIgnoreCase))
                    {
                        if (state.Equals("baseline-pressed", StringComparison.OrdinalIgnoreCase))
                        {
                            inspector.AcceptCommissioningBaselineButton.BringIntoView();
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            interaction.ActivateWindow();
                            interaction.MovePointerToCenter(inspector.AcceptCommissioningBaselineButton);
                            await Task.Delay(100);
                            interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                            interaction.MarkSmokePointerHeld();
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            if (!inspector.AcceptCommissioningBaselineButton.IsPressed)
                            {
                                throw new InvalidOperationException("Baseline accept did not enter pointer-down state.");
                            }
                        }
                        else
                        {
                            viewModel.AcceptCommissioningBaselineCommand.Execute(null);
                            if (viewModel.AcceptedCommissioningBaseline is null)
                            {
                                throw new InvalidOperationException("Commissioning baseline was not accepted.");
                            }
                            if (state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                                || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                            {
                                var targetIndex = state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase)
                                    ? 1
                                    : 0;
                                viewModel.MultiAxisCommissioningRecipe.Targets[targetIndex].TargetPosition += 1;
                                var changedHistoryCount = viewModel.CommissioningResultHistory.Entries.Length;
                                viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                                for (var attempt = 0; attempt < 200
                                     && (viewModel.IsCommissioningValidationRunning
                                         || viewModel.CommissioningResultHistory.Entries.Length <= changedHistoryCount);
                                     attempt++)
                                {
                                    await Task.Delay(25);
                                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                                }
                                var expectedAxisId = viewModel.MultiAxisCommissioningRecipe.Targets[targetIndex].AxisId;
                                if (viewModel.CommissioningBaselineComparison?.FirstMismatch?.TargetId != expectedAxisId)
                                {
                                    throw new InvalidOperationException(
                                        $"Commissioning baseline mismatch did not target axis '{expectedAxisId}'.");
                                }
                                viewModel.NavigateToCommissioningMismatchCommand.Execute(null);
                                if (viewModel.Layout.SelectedItem?.Id != expectedAxisId)
                                {
                                    throw new InvalidOperationException(
                                        $"Commissioning mismatch navigation did not select axis '{expectedAxisId}'.");
                                }
                            }
                        }
                    }
                    if (state.Equals("baseline-mismatch", StringComparison.OrdinalIgnoreCase)
                        || state.Equals("baseline-mismatch-x", StringComparison.OrdinalIgnoreCase))
                    {
                        inspector.NavigateCommissioningMismatchButton.BringIntoView();
                    }
                    else if (state.Equals("baseline-accepted", StringComparison.OrdinalIgnoreCase))
                    {
                        inspector.AcceptCommissioningBaselineButton.BringIntoView();
                    }
                    else
                    {
                        inspector.CommissioningResultHistoryList.BringIntoView();
                    }
                }
                else if (state.Equals("validated", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("validating", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel.MultiAxisCommissioningRecipe.ValidationRepetitions =
                        state.Equals("validating", StringComparison.OrdinalIgnoreCase) ? 100 : 3;
                    viewModel.ValidateMultiAxisCommissioningRecipeCommand.Execute(null);
                    for (var attempt = 0; attempt < 200; attempt++)
                    {
                        var ready = state.Equals("validating", StringComparison.OrdinalIgnoreCase)
                            ? viewModel.IsCommissioningValidationRunning
                            : !viewModel.IsCommissioningValidationRunning
                                && viewModel.LatestCommissioningResult is not null;
                        if (ready)
                        {
                            break;
                        }
                        await Task.Delay(25);
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    if (state.Equals("validating", StringComparison.OrdinalIgnoreCase)
                        ? !viewModel.IsCommissioningValidationRunning
                        : viewModel.LatestCommissioningResult is not { IsSuccess: true })
                    {
                        throw new InvalidOperationException("Recipe validation smoke state was not reached.");
                    }
                }
                else if (!state.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.ActivateWindow();
                    interaction.MovePointerToCenter(inspector.RunMultiAxisRecipeButton);
                    await Task.Delay(100);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!inspector.RunMultiAxisRecipeButton.IsMouseOver)
                    {
                        throw new InvalidOperationException("Recipe Run did not enter pointer-hover state.");
                    }
                    if (state.Equals("run-pressed", StringComparison.OrdinalIgnoreCase))
                    {
                        interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        interaction.MarkSmokePointerHeld();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        if (!inspector.RunMultiAxisRecipeButton.IsPressed)
                        {
                            throw new InvalidOperationException("Recipe Run did not enter pointer-down state.");
                        }
                    }
                }
                break;
            case "running":
                viewModel.IsRunMode = true;
                viewModel.RunMultiAxisCommissioningRecipeCommand.Execute(null);
                for (var attempt = 0; attempt < 80 &&
                     !viewModel.MultiAxisCommissioningRecipe.Targets.Any(target =>
                         target.RuntimeState == AxisState.Moving); attempt++)
                {
                    await Task.Delay(25);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                inspector.RunInspectorScrollViewer.ScrollToTop();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-multi-axis-recipe-state '{state}'. " +
                    "Expected design, design-focus, design-popup, ready, run-hover, run-pressed, running, " +
                    "validation-focus, validation-hover, validation-pressed, validated, validating, " +
                    "history-selected, baseline-pressed, baseline-accepted, baseline-mismatch, or baseline-mismatch-x.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }
}
