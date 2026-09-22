using System.Collections.Concurrent;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
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
        Assert.Empty(workflow.CurrentTransactions);
        Assert.Empty(workflow.TransactionDiagnostics);
        Assert.Null(workflow.LatestTransaction);
        Assert.Null(workflow.LatestResult);
    }

    [Fact]
    public async Task RefreshAsyncClassifiesMalformedResultAsReadErrorWithoutFailingRefresh()
    {
        using var fixture = new TestRoot();
        var handoff = fixture.PublishHandoff();
        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc,
            handoff.Context.ConsumerBuild,
            IntegrationAcknowledgementStatus.Accepted,
            null);
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"));
        File.WriteAllBytes(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(acknowledgement));
        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{ malformed-result }");
        using var workflow = CreateWorkflow(fixture.ExchangeRoot, () => Task.CompletedTask);

        var transactionCount = await workflow.RefreshAsync();

        Assert.Equal(1, transactionCount);
        Assert.Null(workflow.LatestResult);
        Assert.NotNull(workflow.ResultReadError);
    }

    [Fact]
    public async Task RefreshAsyncPublishesCurrentProjectTransactionHistorySnapshot()
    {
        using var fixture = new TestRoot();
        var first = fixture.PublishHandoff();
        var second = fixture.PublishHandoff();
        using var workflow = CreateWorkflow(fixture.ExchangeRoot, () => Task.CompletedTask);

        var transactionCount = await workflow.RefreshAsync();

        Assert.Equal(2, transactionCount);
        Assert.Equal(2, workflow.TransactionCount);
        Assert.Equal(
            new[] { first.TransactionId, second.TransactionId }.OrderBy(id => id),
            workflow.CurrentTransactions.Select(transaction => transaction.Handoff.TransactionId).OrderBy(id => id));
        Assert.All(workflow.CurrentTransactions, transaction =>
        {
            Assert.False(transaction.HasAcknowledgement);
            Assert.False(transaction.HasResult);
            });
    }

    [Fact]
    public async Task RefreshAsyncDoesNotPublishAfterProjectContextChanges()
    {
        using var fixture = new TestRoot();
        fixture.PublishHandoff();
        var projectId = "project-1";
        var synchronizationContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(synchronizationContext);
        try
        {
            using var workflow = new MachineIntegrationResultObservationWorkflow(
                () => fixture.ExchangeRoot,
                () => projectId,
                () => true,
                () => false,
                () => Task.CompletedTask,
                operation => operation(),
                _ => { });
            var refreshTask = workflow.RefreshAsync();

            projectId = "project-2";
            Assert.True(workflow.RefreshContext());
            synchronizationContext.RunNext();

            Assert.Null(await refreshTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(workflow.CurrentTransactions);
            Assert.Empty(workflow.TransactionDiagnostics);
            Assert.Null(workflow.LatestTransaction);
            Assert.Equal(0, workflow.TransactionCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task RefreshAsyncDoesNotPublishAfterReset()
    {
        using var fixture = new TestRoot();
        fixture.PublishHandoff();
        var synchronizationContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(synchronizationContext);
        try
        {
            using var workflow = CreateWorkflow(fixture.ExchangeRoot, () => Task.CompletedTask);
            var refreshTask = workflow.RefreshAsync();

            workflow.Reset();
            synchronizationContext.RunNext();

            Assert.Null(await refreshTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(workflow.CurrentTransactions);
            Assert.Empty(workflow.TransactionDiagnostics);
            Assert.Null(workflow.LatestTransaction);
            Assert.Equal(0, workflow.TransactionCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public async Task RefreshAsyncPublishesReadOnlyTransactionDiagnostics()
    {
        using var fixture = new TestRoot();
        var published = fixture.PublishHandoff();
        var transactionsRoot = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        Directory.CreateDirectory(transactionsRoot);
        Directory.CreateDirectory(Path.Combine(
            transactionsRoot,
            $".{Guid.NewGuid():D}.{Guid.NewGuid():N}.staging"));
        Directory.CreateDirectory(Path.Combine(transactionsRoot, "unexpected"));
        Directory.CreateDirectory(Path.Combine(
            transactionsRoot,
            ".quarantine",
            $"{Guid.NewGuid():D}.20260910000000000.{Guid.NewGuid():N}"));
        using var workflow = CreateWorkflow(fixture.ExchangeRoot, () => Task.CompletedTask);

        await workflow.RefreshAsync();

        Assert.Null(workflow.DiagnosticReadError);
        Assert.Contains(
            workflow.TransactionDiagnostics,
            diagnostic => diagnostic.TransactionId == published.TransactionId
                && diagnostic.State == MachineIntegrationTransactionState.Published);
        Assert.Contains(
            workflow.TransactionDiagnostics,
            diagnostic => diagnostic.State == MachineIntegrationTransactionState.Staging);
        Assert.Contains(
            workflow.TransactionDiagnostics,
            diagnostic => diagnostic.State == MachineIntegrationTransactionState.Invalid);
        Assert.Contains(
            workflow.TransactionDiagnostics,
            diagnostic => diagnostic.State == MachineIntegrationTransactionState.Quarantined);
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

    [Fact]
    public async Task ResultWatcherExceptionUsesInjectedUiDispatchBoundary()
    {
        using var fixture = new TestRoot();
        var dispatchDepth = 0;
        var exceptionTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var workflow = new MachineIntegrationResultObservationWorkflow(
            () => fixture.ExchangeRoot,
            () => "project-1",
            () => true,
            () => false,
            () => throw new IOException("watcher-failure"),
            async operation =>
            {
                Interlocked.Increment(ref dispatchDepth);
                try
                {
                    await operation();
                }
                finally
                {
                    Interlocked.Decrement(ref dispatchDepth);
                }
            },
            exception =>
            {
                Assert.Equal(1, Volatile.Read(ref dispatchDepth));
                exceptionTcs.TrySetResult(exception);
            });

        workflow.ConfigureWatcher();
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(transactionDirectory);
        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{}");

        var exception = await exceptionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<IOException>(exception);
        Assert.Equal(0, Volatile.Read(ref dispatchDepth));
    }

    [Fact]
    public async Task DisposingWorkflowCancelsPendingResultRefresh()
    {
        using var fixture = new TestRoot();
        var refreshCount = 0;
        using var workflow = CreateWorkflow(
            fixture.ExchangeRoot,
            () =>
            {
                Interlocked.Increment(ref refreshCount);
                return Task.CompletedTask;
            });

        workflow.ConfigureWatcher();
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(transactionDirectory);
        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{}");
        workflow.Dispose();

        await Task.Delay(250);

        Assert.Equal(0, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task QueuedUiRefreshDoesNotRunAfterWorkflowDispose()
    {
        using var fixture = new TestRoot();
        var refreshCount = 0;
        var queuedRefresh = new TaskCompletionSource<Func<Task>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var workflow = new MachineIntegrationResultObservationWorkflow(
            () => fixture.ExchangeRoot,
            () => "project-1",
            () => true,
            () => false,
            () =>
            {
                Interlocked.Increment(ref refreshCount);
                return Task.CompletedTask;
            },
            operation =>
            {
                queuedRefresh.TrySetResult(operation);
                return Task.CompletedTask;
            },
            _ => { });

        workflow.ConfigureWatcher();
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(transactionDirectory);
        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{}");

        var refreshOperation = await queuedRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.Dispose();
        await refreshOperation();

        Assert.Equal(0, Volatile.Read(ref refreshCount));
    }

    [Fact]
    public async Task DisposingBetweenObservationReadsSuppressesLateAcknowledgementState()
    {
        using var fixture = new TestRoot();
        var handoff = fixture.PublishHandoff();
        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc,
            handoff.Context.ConsumerBuild,
            IntegrationAcknowledgementStatus.Accepted,
            null);
        File.WriteAllBytes(
            Path.Combine(
                fixture.ExchangeRoot,
                IntegrationTransactionLayout.TransactionsDirectoryName,
                handoff.TransactionId.ToString("D"),
                IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(acknowledgement));

        var synchronizationContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(synchronizationContext);
        try
        {
            using var workflow = CreateWorkflow(fixture.ExchangeRoot, () => Task.CompletedTask);
            var refreshTask = workflow.RefreshAsync();

            synchronizationContext.RunNext();
            synchronizationContext.WaitForPost();
            workflow.Dispose();
            synchronizationContext.RunNext();

            var refreshResult = await refreshTask;
            Assert.Null(refreshResult);
            Assert.Null(workflow.LatestAcknowledgement);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
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
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(ExchangeRoot);
            Directory.CreateDirectory(SourceRoot);
            File.WriteAllText(ProjectPath, "{\"schema\":\"machine-project/1.0\"}");
            File.WriteAllBytes(SourcePath, [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(RecipePath, "{\"tool\":\"test\"}");
        }

        private string Root { get; }

        public string ExchangeRoot { get; }

        private string SourceRoot { get; }

        private string ProjectPath => Path.Combine(SourceRoot, "machine.ovmachine");

        private string SourcePath => Path.Combine(SourceRoot, "inspection-source.png");

        private string RecipePath => Path.Combine(SourceRoot, "inspection-recipe.json");

        public IntegrationHandoffV2 PublishHandoff() =>
            MachineIntegrationHandoffPublisher.PublishAsync(
                    ExchangeRoot,
                    new MachineInspectionHandoffRequest(
                        "project-1",
                        "machine-project/1.0",
                        "sequence-1",
                        "inspect-step",
                        "camera-virtual",
                        "acquisition-1",
                        "frame-1",
                        "px",
                        ProjectPath,
                        SourcePath,
                        RecipePath,
                        IntegrationInspectionModality.TwoD,
                        IntegrationInspectionInputKind.Image,
                        new IntegrationApplicationIdentity(
                            IntegrationApplicationIds.MachineStudio,
                            "1.0.0",
                            new string('1', 40),
                            IntegrationSourceState.Clean),
                        new IntegrationApplicationIdentity(
                            IntegrationApplicationIds.TwoDStudio,
                            "2.1.0",
                            new string('2', 40),
                            IntegrationSourceState.Clean)))
                .GetAwaiter()
                .GetResult();

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Enqueue((callback, state));

        public void WaitForPost()
        {
            if (!SpinWait.SpinUntil(() => !_callbacks.IsEmpty, TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("The observation continuation was not queued.");
            }
        }

        public void RunNext()
        {
            WaitForPost();
            if (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }
    }
}
