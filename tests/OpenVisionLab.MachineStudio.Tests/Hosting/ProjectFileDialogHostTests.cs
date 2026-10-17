using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.View.Shell;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class ProjectFileDialogHostTests
{
    private readonly StudioUiTestHost _ui;

    public ProjectFileDialogHostTests(StudioUiTestHost ui) => _ui = ui;

    [Theory]
    [InlineData("open", false)]
    [InlineData("save", false)]
    [InlineData("copy", true)]
    public async Task CancelReturnsKeyboardFocusToAVisibleOwner(string entry, bool disableLauncher)
    {
        await _ui.InvokeAsync(() =>
        {
            var launcher = new Button { Content = "File action" };
            var window = new Window { Content = launcher, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 640, 480);
            window.Show();
            try
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                launcher.Focus();
                Assert.Same(launcher, Keyboard.FocusedElement);
                var host = new ProjectFileDialogHost((dialog, owner) =>
                {
                    Assert.Same(window, owner);
                    if (disableLauncher) launcher.IsEnabled = false;
                    FocusManager.SetFocusedElement(FocusManager.GetFocusScope(launcher), null);
                    Keyboard.ClearFocus();
                    Assert.Null(Keyboard.FocusedElement);
                    return false;
                });
                var selected = entry switch
                {
                    "open" => host.SelectProjectToOpen(),
                    "save" => host.SelectProjectSaveAs("Owned"),
                    _ => host.SelectRecipeCopyDestination("owned.ovmachine")
                };
                Assert.Null(selected);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Same(disableLauncher ? (UIElement)window : launcher, Keyboard.FocusedElement);
                Assert.True(window.IsKeyboardFocusWithin);
                Assert.True(SmokeDpiTestHook.CaptureMonitorEvidence(window).WindowIntersectsMonitor);
            }
            finally { window.Close(); }
            return true;
        });
    }
}
