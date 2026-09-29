using System.IO;
using System.Windows;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.MachineStudio.Smoke;

namespace OpenVisionLab.MachineStudio;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            if (MachineIntegrationExeSmoke.IsRequested(e.Args))
            {
                var exitCode = await MachineIntegrationExeSmoke.RunAsync(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (DirectExeSmokeHost.IsRequested(e.Args))
            {
                await DirectExeSmokeHost.RunAsync(e.Args);
                return;
            }

            StartInteractiveApplication();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Machine Studio startup failed: {exception}");
            Shutdown(2);
        }
    }

    private static void StartInteractiveApplication()
    {
        var useUiTestMonitor =
            Environment.GetEnvironmentVariable("OPENVISIONLAB_MACHINE_STUDIO_UI_TEST_MONITOR") == "1";
        var bundledSamplePath = Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "R19InspectionFlow.ovmachine");
        var largeLayoutSamplePath = Path.Combine(
            AppContext.BaseDirectory,
            "Samples",
            "LargeLayoutExploration.ovmachine");

        var window = new ShellWindow
        {
            DataContext = new MainViewModel(
                startupSamplePath: File.Exists(bundledSamplePath) ? bundledSamplePath : null,
                largeLayoutSamplePath: File.Exists(largeLayoutSamplePath) ? largeLayoutSamplePath : null),
            Width = 1280,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (useUiTestMonitor)
        {
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
        }
        window.Show();
        if (useUiTestMonitor)
        {
            SmokeDpiTestHook.Apply(window, 100, 1280, 760);
        }
    }
}
