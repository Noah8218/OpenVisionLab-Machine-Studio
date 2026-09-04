using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationResultObservationWorkflowTests
{
    [Fact]
    public async Task RefreshAsyncReadsCurrentProjectTransactionsWithoutViewModel()
    {
        using var fixture = new TestRoot();
        using var workflow = CreateWorkflow(fixture.ExchangeRoot, () => Task.CompletedTask);

        var transactionCount = await workflow.RefreshAsync();

        Assert.Equal(0, transactionCount);
        Assert.Equal(0, workflow.TransactionCount);
        Assert.Null(workflow.LatestTransaction);
        Assert.Null(workflow.LatestResult);
    }

    [Fact]
    public async Task ResultWatcherSchedulesInjectedRefreshWithoutViewModel()
    {
        using var fixture = new TestRoot();
        var refreshCount = 0;
        Exception? watcherException = null;
        using var workflow = CreateWorkflow(
            fixture.ExchangeRoot,
            () =>
            {
                Interlocked.Increment(ref refreshCount);
                return Task.CompletedTask;
            },
            exception => watcherException = exception);

        workflow.ConfigureWatcher();
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(transactionDirectory);
        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{}");

        await WaitForAsync(() => Volatile.Read(ref refreshCount) > 0);

        Assert.Null(watcherException);
    }

    private static MachineIntegrationResultObservationWorkflow CreateWorkflow(
        string exchangeRoot,
        Func<Task> refreshAsync,
        Action<Exception>? handleAutomaticRefreshException = null) =>
        new(
            () => exchangeRoot,
            () => "project-1",
            () => true,
            () => false,
            refreshAsync,
            operation => operation(),
            handleAutomaticRefreshException ?? (_ => { }));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "The result watcher did not schedule the injected refresh.");
    }

    private sealed class TestRoot : IDisposable
    {
        public TestRoot()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "machine-integration-result-observation-tests",
                Guid.NewGuid().ToString("N"));
            ExchangeRoot = Path.Combine(Root, "exchange");
            Directory.CreateDirectory(ExchangeRoot);
        }

        private string Root { get; }

        public string ExchangeRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
