using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Shell;
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
        var toggle = FindVisualDescendant<ToggleButton>(
            window,
            candidate => string.Equals(
                candidate.Name,
                "EvidenceDrawerToggle",
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Evidence drawer toggle was not available.");
        var scrollToLatest = false;

        switch (state.ToLowerInvariant())
        {
            case "collapsed":
                toggle.IsChecked = false;
                break;
            case "expanded":
                toggle.IsChecked = true;
                break;
            case "expanded-latest":
                toggle.IsChecked = true;
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
                toggle.IsChecked = true;
                scrollToLatest = true;
                break;
            case "focus":
                toggle.IsChecked = false;
                window.Activate();
                toggle.Focus();
                break;
            case "hover":
                toggle.IsChecked = false;
                interaction.MovePointerToCenter(toggle);
                break;
            case "pressed":
                toggle.IsChecked = false;
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
                    "Expected collapsed, expanded, expanded-latest, expanded-retention, focus, hover, or pressed.");
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
        if (state.Equals("pressed", StringComparison.OrdinalIgnoreCase) && !toggle.IsPressed)
        {
            throw new InvalidOperationException("Evidence drawer did not enter the pointer-down state.");
        }
        if (!ReferenceEquals(snapshotBefore, viewModel.SceneSnapshots.Latest))
        {
            throw new InvalidOperationException("Evidence drawer interaction changed the runtime snapshot.");
        }
        Console.WriteLine($"Evidence drawer visual state applied: {state}");
    }
}
