using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class SmokeLayoutHistoryVerifierTests
{
    private static string SamplePath => Path.Combine(
        AppContext.BaseDirectory,
        "Samples",
        "AutomaticTransferCell.ovmachine");

    [Fact]
    public async Task VerifiesLayoutHistoryAndClipboardRoundTrip()
    {
        var evidenceRoot = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\pl-0043-layout-history-verifier-20260826",
            "focused");
        Directory.CreateDirectory(evidenceRoot);
        var reportPath = Path.Combine(evidenceRoot, "layout-history-report.json");

        var report = await RunOnStaAsync(async () =>
        {
            var project = new ProjectDocumentStore().Load(File.ReadAllText(SamplePath));
            using var viewModel = new MainViewModel(project);

            var result = await SmokeLayoutHistoryVerifier.VerifyAsync(viewModel, reportPath);
            result.Save(reportPath);

            Assert.True(result.IsValid, string.Join(", ", result.Failures));
            Assert.NotEmpty(result.PastedComponentIds);
            Assert.All(result.Checks, check => Assert.True(check.Value, check.Key));
            Assert.True(viewModel.IsDesignMode);
            Assert.False(viewModel.IsRunning);
            return result;
        });

        Assert.True(File.Exists(reportPath));

        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.True(json.RootElement.TryGetProperty("checks", out _));
        Assert.True(json.RootElement.TryGetProperty("pastedComponentIds", out _));
    }

    private static Task<T> RunOnStaAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = RunAsync();
            Dispatcher.Run();

            async Task RunAsync()
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
