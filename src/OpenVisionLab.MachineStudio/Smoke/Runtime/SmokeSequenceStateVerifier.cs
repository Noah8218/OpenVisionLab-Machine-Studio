using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeSequenceStateVerifier
{
    private const uint MouseEventLeftDown = 0x0002;

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        var editor = FindVisualDescendant<SequenceEditorView>(window)
            ?? throw new InvalidOperationException(
                "The Sequence document tab must be visible for a sequence-state smoke.");

        if (state.StartsWith("subsequence", StringComparison.OrdinalIgnoreCase))
        {
            var project = viewModel.ProjectTree.Roots.Single().Model as MachineProjectDocument
                ?? throw new InvalidOperationException("The current machine project was unavailable.");
            var parent = project.Sequences.FirstOrDefault()
                ?? throw new InvalidOperationException("The smoke project did not contain a parent sequence.");
            const string childId = "smoke-subsequence";
            const string callStepId = "smoke-call-subsequence";
            var child = project.Sequences.FirstOrDefault(sequence =>
                string.Equals(sequence.Id, childId, StringComparison.Ordinal));
            if (child is null)
            {
                child = new SequenceDefinition
                {
                    Id = childId,
                    Name = "Smoke Child Sequence",
                    Steps =
                    [
                        new SequenceStepDefinition
                        {
                            Id = "smoke-child-complete",
                            Name = "Child complete",
                            Action = SequenceStepAction.Complete
                        }
                    ]
                };
                project.Sequences.Add(child);
            }

            var existingCall = parent.Steps.FirstOrDefault(step =>
                string.Equals(step.Id, callStepId, StringComparison.Ordinal));
            if (existingCall is null)
            {
                var terminalIndex = parent.Steps.FindIndex(step => step.Action == SequenceStepAction.Complete);
                if (terminalIndex < 0)
                {
                    throw new InvalidOperationException("The smoke parent sequence did not contain a Complete step.");
                }

                var terminal = parent.Steps[terminalIndex];
                var predecessor = terminalIndex > 0 ? parent.Steps[terminalIndex - 1] : null;
                existingCall = new SequenceStepDefinition
                {
                    Id = callStepId,
                    Name = "Call smoke child",
                    Action = SequenceStepAction.CallSubsequence,
                    TargetId = child.Id,
                    NextStepId = terminal.Id
                };
                if (predecessor is not null)
                {
                    predecessor.NextStepId = callStepId;
                }
                parent.Steps.Insert(terminalIndex, existingCall);
            }

            viewModel.SequenceEditor.Load(project);
            viewModel.SequenceEditor.SelectStep(parent.Id, callStepId);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var comboBox = FindVisualDescendant<ComboBox>(editor, candidate =>
                    string.Equals(candidate.Name, "SequenceTargetComboBox", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The named sequence target ComboBox was unavailable.");
            var targetIds = comboBox.Items
                .OfType<SequenceAuthoringTarget>()
                .Select(target => target.Id)
                .ToArray();
            if (!comboBox.IsVisible
                || !comboBox.IsEnabled
                || !string.Equals(comboBox.SelectedValue?.ToString(), child.Id, StringComparison.Ordinal)
                || !targetIds.Contains(child.Id, StringComparer.Ordinal)
                || targetIds.Contains(parent.Id, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The subsequence target editor did not expose only the other sequence target.");
            }

            window.Activate();
            comboBox.Focus();
            if (state.Equals("subsequence-focus", StringComparison.OrdinalIgnoreCase))
            {
                Keyboard.Focus(comboBox);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!comboBox.IsKeyboardFocusWithin)
                {
                    throw new InvalidOperationException("The subsequence target ComboBox did not receive keyboard focus.");
                }
            }
            else if (state.Equals("subsequence-pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.ActivateWindow();
                interaction.MovePointerToCenter(comboBox);
                await Task.Delay(100);
                if (!comboBox.IsMouseOver)
                {
                    throw new InvalidOperationException("The subsequence target ComboBox did not enter hover state.");
                }

                interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (Mouse.LeftButton != MouseButtonState.Pressed || !comboBox.IsMouseCaptureWithin)
                {
                    throw new InvalidOperationException("The subsequence target ComboBox did not enter pointer-down state.");
                }
            }
            else if (state.Equals("subsequence-mouse-leave", StringComparison.OrdinalIgnoreCase))
            {
                interaction.MovePointerToCenter(comboBox);
                await Task.Delay(100);
                if (!comboBox.IsMouseOver)
                {
                    throw new InvalidOperationException("The subsequence target ComboBox did not enter hover state.");
                }

                var outside = window.PointToScreen(new Point(10, 10));
                interaction.SetCursorPosition(
                    checked((int)Math.Round(outside.X)),
                    checked((int)Math.Round(outside.Y)));

                await Task.Delay(100);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (comboBox.IsMouseOver
                    || !string.Equals(comboBox.SelectedValue?.ToString(), child.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The subsequence target ComboBox did not recover after mouse leave.");
                }
            }
            else if (state.Equals("subsequence-popup", StringComparison.OrdinalIgnoreCase))
            {
                comboBox.IsDropDownOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!comboBox.IsDropDownOpen)
                {
                    throw new InvalidOperationException("The subsequence target ComboBox popup did not open.");
                }

                var windowRoot = PresentationSource.FromVisual(window)?.RootVisual;
                var popup = PresentationSource.CurrentSources
                    .Cast<PresentationSource>()
                    .Select(source => source.RootVisual)
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(root =>
                        !ReferenceEquals(root, windowRoot)
                        && root.IsVisible
                        && root.ActualWidth > 0
                        && root.ActualHeight > 0)
                    ?? throw new InvalidOperationException(
                        "The subsequence target ComboBox popup content was unavailable.");
                interaction.SetPopupContent(popup);
            }
            else if (!state.Equals("subsequence", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-sequence-state '{state}'. Expected subsequence, " +
                    "subsequence-focus, subsequence-pressed, subsequence-mouse-leave, or subsequence-popup.");
            }
        }
        else if (state.Equals("focus", StringComparison.OrdinalIgnoreCase))
        {
            var textBox = FindVisualDescendant<TextBox>(editor, candidate =>
                    candidate.IsVisible &&
                    candidate.IsVisible &&
                    candidate.IsEnabled &&
                    !candidate.IsReadOnly &&
                    candidate.Focusable)
                ?? throw new InvalidOperationException("No sequence TextBox was visible.");
            window.Activate();
            textBox.Focus();
            Keyboard.Focus(textBox);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!textBox.IsKeyboardFocusWithin)
            {
                throw new InvalidOperationException("Sequence TextBox did not receive keyboard focus.");
            }
        }
        else if (state.Equals("hover", StringComparison.OrdinalIgnoreCase))
        {
            var row = FindVisualDescendant<DataGridRow>(editor, candidate =>
                    candidate.IsVisible && !candidate.IsSelected)
                ?? throw new InvalidOperationException(
                    "No unselected sequence DataGrid row was visible.");
            window.Activate();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var center = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
            interaction.SetCursorPosition(
                checked((int)Math.Round(center.X)),
                checked((int)Math.Round(center.Y)));

            await Task.Delay(100);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!row.IsMouseOver)
            {
                throw new InvalidOperationException("Sequence row did not enter the pointer-hover state.");
            }
        }
        else if (state.Equals("popup", StringComparison.OrdinalIgnoreCase))
        {
            var comboBox = FindVisualDescendant<ComboBox>(editor)
                ?? throw new InvalidOperationException("No sequence ComboBox was visible.");
            window.Activate();
            comboBox.Focus();
            comboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!comboBox.IsDropDownOpen)
            {
                throw new InvalidOperationException("Sequence ComboBox popup did not open.");
            }
        }
        else if (state.Equals("target-popup", StringComparison.OrdinalIgnoreCase))
        {
            var comboBox = FindVisualDescendant<ComboBox>(editor, candidate =>
                    string.Equals(candidate.Name, "SequenceTargetComboBox", StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "The named sequence target ComboBox was unavailable.");
            window.Activate();
            comboBox.Focus();
            comboBox.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!comboBox.IsVisible
                || !comboBox.IsEnabled
                || !comboBox.IsDropDownOpen
                || !string.Equals(comboBox.SelectedValue?.ToString(), "camera-1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The selected virtual-camera target popup did not open with camera-1.");
            }

            var windowRoot = PresentationSource.FromVisual(window)?.RootVisual;
            var popup = PresentationSource.CurrentSources
                .Cast<PresentationSource>()
                .Select(source => source.RootVisual)
                .OfType<FrameworkElement>()
                .FirstOrDefault(root =>
                    !ReferenceEquals(root, windowRoot)
                    && root.IsVisible
                    && root.ActualWidth > 0
                    && root.ActualHeight > 0)
                ?? throw new InvalidOperationException(
                    "The sequence target ComboBox popup content was unavailable.");
            interaction.SetPopupContent(popup);
        }
        else if (state.Equals("validation", StringComparison.OrdinalIgnoreCase))
        {
            var step = viewModel.SequenceEditor.SelectedStep
                ?? throw new InvalidOperationException("No sequence step was selected.");
            step.NextStepId = "missing-smoke-step";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (viewModel.SequenceEditor.ValidationMessages.Count == 0)
            {
                throw new InvalidOperationException("Invalid sequence input produced no validation state.");
            }
        }
        else if (state.StartsWith("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            viewModel.SequenceEditor.SelectStep("wait-cylinder-extended");
            var step = viewModel.SequenceEditor.SelectedStep
                ?? throw new InvalidOperationException("The checkpoint smoke step was not available.");
            step.HasExpectedState = true;
            step.ExpectedTargetId = "process-cylinder";
            step.ExpectedState = "Extended";
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!step.HasExpectedState
                || step.ExpectedTargetId != "process-cylinder"
                || step.ExpectedState != "Extended"
                || !step.AvailableExpectedStates.Contains("Extended", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The expected-state checkpoint editor was not populated.");
            }

            var checkBox = FindVisualDescendant<CheckBox>(editor, candidate =>
                    candidate.IsVisible
                    && candidate.IsChecked == true)
                ?? throw new InvalidOperationException("The expected-state checkpoint checkbox was not visible.");
            if (state.Equals("checkpoint-focus", StringComparison.OrdinalIgnoreCase))
            {
                window.Activate();
                checkBox.Focus();
                Keyboard.Focus(checkBox);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!checkBox.IsKeyboardFocused)
                {
                    throw new InvalidOperationException("The checkpoint checkbox did not receive keyboard focus.");
                }
            }
            else if (state.Equals("checkpoint-hover", StringComparison.OrdinalIgnoreCase)
                     || state.Equals("checkpoint-pressed", StringComparison.OrdinalIgnoreCase))
            {
                interaction.ActivateWindow();
                interaction.MovePointerToCenter(checkBox);
                await Task.Delay(150);
                if (!checkBox.IsMouseOver)
                {
                    throw new InvalidOperationException("The checkpoint checkbox did not enter hover state.");
                }
                if (state.Equals("checkpoint-pressed", StringComparison.OrdinalIgnoreCase))
                {
                    interaction.MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    interaction.MarkSmokePointerHeld();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (!checkBox.IsPressed)
                    {
                        throw new InvalidOperationException("The checkpoint checkbox did not enter pointer-down state.");
                    }
                }
            }
            else if (state.Equals("checkpoint-popup", StringComparison.OrdinalIgnoreCase))
            {
                var comboBox = FindVisualDescendant<ComboBox>(editor, candidate =>
                        candidate.IsVisible
                        && candidate.IsEnabled
                        && candidate.Items.OfType<string>().Contains("Extended", StringComparer.Ordinal))
                    ?? throw new InvalidOperationException("The expected-state ComboBox was not visible.");
                window.Activate();
                comboBox.Focus();
                comboBox.IsDropDownOpen = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!comboBox.IsDropDownOpen)
                {
                    throw new InvalidOperationException("The expected-state ComboBox popup did not open.");
                }
                var windowRoot = PresentationSource.FromVisual(window)?.RootVisual;
                var popup = PresentationSource.CurrentSources
                    .Cast<PresentationSource>()
                    .Select(source => source.RootVisual)
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(root =>
                        !ReferenceEquals(root, windowRoot)
                        && root.IsVisible
                        && root.ActualWidth > 0
                        && root.ActualHeight > 0)
                    ?? throw new InvalidOperationException(
                        "The expected-state ComboBox popup content was unavailable.");
                interaction.SetPopupContent(popup);
            }
            else if (state.Equals("checkpoint-disabled", StringComparison.OrdinalIgnoreCase))
            {
                viewModel.IsRunMode = true;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (checkBox.IsEnabled)
                {
                    throw new InvalidOperationException("The checkpoint editor remained enabled in Run mode.");
                }
            }
            else if (state.Equals("checkpoint-validation", StringComparison.OrdinalIgnoreCase))
            {
                step.ExpectedState = string.Empty;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (viewModel.SequenceEditor.ValidationMessages.Count == 0)
                {
                    throw new InvalidOperationException("An incomplete checkpoint produced no validation state.");
                }
            }
            else if (!state.Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unsupported --smoke-sequence-state '{state}'. Expected checkpoint, " +
                    "checkpoint-focus, checkpoint-hover, checkpoint-pressed, checkpoint-popup, " +
                    "checkpoint-disabled, or checkpoint-validation.");
            }
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported --smoke-sequence-state '{state}'. " +
                "Expected focus, hover, popup, validation, checkpoint, or subsequence.");
        }

        Console.WriteLine($"Sequence visual state applied: {state}");
    }
}
