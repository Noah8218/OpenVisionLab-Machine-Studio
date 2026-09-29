using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Diagnostics;
using OpenVisionLab.MachineStudio.ViewModel;
using static OpenVisionLab.MachineStudio.SmokeVisualTreeQuery;

namespace OpenVisionLab.MachineStudio;

internal static class SmokeEvidenceDrawerStateVerifier
{
    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static async Task ApplyAsync(
        ShellWindow window,
        MainViewModel viewModel,
        string state,
        SmokeUiInteraction interaction)
    {
        var snapshotBefore = viewModel.SceneSnapshots.Latest;
        var dirtyBefore = viewModel.HasUnsavedChanges;
        var panel = FindVisualDescendant<EventJournalView>(window)
            ?? throw new InvalidOperationException("Run records panel was unavailable.");
        var toggle = FindVisualDescendant<ToggleButton>(
            window,
            candidate => string.Equals(
                candidate.Name,
                "EvidenceDrawerToggle",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Evidence drawer toggle was not available.");
        var scrollToLatest = false;
        viewModel.Navigation.SelectedEvidenceTabIndex = 3;

        switch (state.ToLowerInvariant())
        {
            case "collapsed":
                viewModel.Navigation.IsEvidenceExpanded = false;
                break;
            case "expanded":
                viewModel.Navigation.IsEvidenceExpanded = true;
                break;
            case "timeline":
            case "timeline-empty":
            case "alarms":
            case "io":
                viewModel.Navigation.IsEvidenceExpanded = true;
                viewModel.Navigation.SelectedEvidenceTabIndex = state.ToLowerInvariant() switch
                {
                    "timeline" or "timeline-empty" => 0, "alarms" => 1, _ => 2
                };
                if (state.Equals("timeline-empty", StringComparison.OrdinalIgnoreCase))
                    viewModel.RuntimeDebugger.ClearTimelineCommand.Execute(null);
                break;
            case "expanded-latest":
                viewModel.Navigation.IsEvidenceExpanded = true;
                scrollToLatest = true;
                break;
            case "expanded-retention":
                viewModel.AppendLog(TimeSpan.Zero, "System", "Retention probe expired");
                for (var index = 0; index < MainViewModel.LogMessageRetentionLimit; index++)
                {
                    viewModel.AppendLog(
                        TimeSpan.FromMilliseconds(index),
                        "System",
                        $"Retention probe {index:0000}");
                }
                AssertSmoke(
                    viewModel.LogMessages.Count == MainViewModel.LogMessageRetentionLimit
                    && !viewModel.LogMessages.Any(line => line.Contains("Retention probe expired", StringComparison.Ordinal))
                    && viewModel.LogMessages[^1].Contains("Retention probe 0999", StringComparison.Ordinal),
                    "Evidence journal did not retain exactly the latest bounded window.");
                viewModel.Navigation.IsEvidenceExpanded = true;
                scrollToLatest = true;
                break;
            case "focus":
                viewModel.Navigation.IsEvidenceExpanded = false;
                window.Activate();
                toggle.Focus();
                break;
            case "hover":
                viewModel.Navigation.IsEvidenceExpanded = false;
                interaction.MovePointerToCenter(toggle);
                break;
            case "pressed":
                viewModel.Navigation.IsEvidenceExpanded = false;
                window.Activate();
                toggle.Focus();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                interaction.MovePointerToCenter(toggle);
                await Task.Delay(100);
                interaction.MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                interaction.MarkSmokePointerHeld();
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported --smoke-evidence-state '{state}'. " +
                    "Expected collapsed, expanded, expanded-latest, expanded-retention, timeline, timeline-empty, alarms, io, focus, hover, or pressed.");
        }

        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (scrollToLatest && viewModel.LogMessages.Count > 0)
        {
            var journal = FindVisualDescendant<ListBox>(
                window,
                candidate => ReferenceEquals(candidate.ItemsSource, viewModel.LogMessages))
                ?? throw new InvalidOperationException("Evidence journal was not available.");
            journal.ScrollIntoView(viewModel.LogMessages[^1]);
        }
        await Task.Delay(150);
        window.UpdateLayout();
        var workspaceBottom = window.WorkspaceColumnsGrid.TranslatePoint(
            new Point(0, window.WorkspaceColumnsGrid.ActualHeight), window).Y;
        var panelTop = panel.TranslatePoint(new Point(), window).Y;
        AssertSmoke(workspaceBottom <= panelTop + 1, "Run records overlap the main workspace.");
        AssertSmoke(toggle.IsChecked == viewModel.Navigation.IsEvidenceExpanded,
            "Run records expansion binding disagrees with navigation.");
        if (viewModel.Navigation.IsEvidenceExpanded)
        {
            AssertSmoke(panel.EvidenceTabs.IsVisible && panel.ActualHeight >= 199,
                "Expanded run records do not have their own readable area.");
            AssertSmoke(panel.EvidenceTabs.SelectedIndex == viewModel.Navigation.SelectedEvidenceTabIndex,
                "Run records tab selection binding disagrees with navigation.");
            AssertSmoke(ReferenceEquals(panel.EvidenceIoSignalGrid.ItemsSource, viewModel.DigitalIo.Signals),
                "Current I/O must use the existing signal projection.");
            if (viewModel.Navigation.SelectedEvidenceTabIndex == 0)
            {
                AssertSmoke(panel.DebuggerTimelineEmptyTextBlock.Visibility ==
                    (viewModel.RuntimeDebugger.HasTimeline ? Visibility.Collapsed : Visibility.Visible),
                    "Timeline events and the empty-state message must be mutually exclusive.");
            }
        }
        if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase) && !toggle.IsPressed)
        {
            throw new InvalidOperationException("Evidence drawer did not enter the pointer-down state.");
        }
        if (!ReferenceEquals(snapshotBefore, viewModel.SceneSnapshots.Latest) || viewModel.HasUnsavedChanges != dirtyBefore)
        {
            throw new InvalidOperationException("Run records navigation changed the runtime snapshot or project dirty state.");
        }
        Console.WriteLine($"Run records layout: workspaceBottom={workspaceBottom:F1}, panelTop={panelTop:F1}, height={panel.ActualHeight:F1}, tab={panel.EvidenceTabs.SelectedIndex}; bindings and runtime isolation passed.");
        Console.WriteLine($"Evidence drawer visual state applied: {state}");
    }
}
