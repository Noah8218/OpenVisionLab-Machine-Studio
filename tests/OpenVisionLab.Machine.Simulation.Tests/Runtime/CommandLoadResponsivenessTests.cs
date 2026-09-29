using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class CommandLoadResponsivenessTests
{
    private static readonly TimeSpan FiniteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContinuousDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PauseTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private const int FiniteBatchSize = 16;
    private const int ContinuousBatchSize = 32;

    [Fact]
    public async Task Mch004_CommandLoadExperiment_ReportsFiniteAndContinuousResponsiveness()
    {
        var results = new List<LoadCaseResult>
        {
            await RunFiniteCaseAsync(commandQueueCapacity: 1, producerCount: 4, commandsPerProducer: 250)
        };

        foreach (var commandQueueCapacity in new[] { 1, 4, SimulationSettings.DefaultCommandQueueCapacity })
        {
            results.Add(await RunContinuousCaseAsync(commandQueueCapacity, producerCount: 16));
        }

        var report = new LoadExperimentReport(DateTimeOffset.UtcNow, results);
        var artifactRoot = Path.Combine(TestStorage.RootPath, "mch-004-command-load-20260915");
        Directory.CreateDirectory(artifactRoot);
        await File.WriteAllTextAsync(
            Path.Combine(artifactRoot, "command-load-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(
            Path.Combine(artifactRoot, "command-load-report.md"),
            BuildMarkdown(report));

        Assert.All(results, result =>
        {
            Assert.True(result.StopCompleted, $"Stop timed out for {result.Name}.");
            Assert.Equal(0, result.PendingAfterStop);
            Assert.Equal(0, result.Faulted);
            Assert.Equal(result.Submitted, result.Completed + result.Cancelled + result.Faulted);
        });
        Assert.Equal(0, results[0].Rejected);
        Assert.Contains(results, result => result.LastTickIndex > result.FirstTickIndex);
    }

    private static async Task<LoadCaseResult> RunFiniteCaseAsync(
        int commandQueueCapacity,
        int producerCount,
        int commandsPerProducer)
    {
        var probe = new LoadProbe();
        using var engine = CreateEngine(commandQueueCapacity, probe);
        await PrepareEngineAsync(engine);
        probe.Begin();

        using var tickCancellation = new CancellationTokenSource();
        var tickObserver = ObserveTicksAsync(engine, probe, tickCancellation.Token);
        try
        {
            var producers = Enumerable.Range(0, producerCount)
                .Select(index => ProduceFiniteAsync(
                    engine,
                    probe,
                    index,
                    commandsPerProducer,
                    FiniteBatchSize))
                .ToArray();
            await Task.WhenAll(producers).WaitAsync(FiniteTimeout);

            var pauseStarted = Stopwatch.GetTimestamp();
            var pauseResult = await engine.EnqueueCommandAsync(new PauseCommand()).WaitAsync(StopTimeout);
            var pauseLatency = probe.ElapsedMilliseconds(Stopwatch.GetTimestamp(), pauseStarted);

            var stopStarted = Stopwatch.GetTimestamp();
            var stopTask = engine.StopAsync();
            var stopCompleted = await CompleteWithinAsync(stopTask, StopTimeout);
            var stopLatency = stopCompleted
                ? (double?)probe.ElapsedMilliseconds(Stopwatch.GetTimestamp(), stopStarted)
                : null;
            return probe.CreateResult(
                "finite-capacity-" + commandQueueCapacity,
                "finite",
                commandQueueCapacity,
                producerCount,
                commandsPerProducer,
                pauseResult,
                pauseTimedOut: false,
                pendingAtControlRequest: probe.Pending,
                pauseLatency,
                stopLatency,
                stopCompleted,
                engine.EventJournal.TotalEventCount);
        }
        finally
        {
            tickCancellation.Cancel();
            await tickObserver;
        }
    }

    private static async Task<LoadCaseResult> RunContinuousCaseAsync(
        int commandQueueCapacity,
        int producerCount)
    {
        var probe = new LoadProbe();
        using var engine = CreateEngine(commandQueueCapacity, probe);
        await PrepareEngineAsync(engine);
        probe.Begin();

        using var tickCancellation = new CancellationTokenSource();
        var tickObserver = ObserveTicksAsync(engine, probe, tickCancellation.Token);
        using var producerCancellation = new CancellationTokenSource();
        var producers = Enumerable.Range(0, producerCount)
            .Select(index => ProduceContinuouslyAsync(
                engine,
                probe,
                index,
                ContinuousBatchSize,
                producerCancellation.Token))
            .ToArray();

        SimulationCommandResult? pauseResult = null;
        var pauseTimedOut = false;
        var stopCompleted = false;
        double? pauseLatency = null;
        double? stopLatency = null;
        var pauseStarted = Stopwatch.GetTimestamp();
        try
        {
            await Task.Delay(ContinuousDuration);
            var pendingAtControlRequest = probe.Pending;
            pauseStarted = Stopwatch.GetTimestamp();
            var pauseTask = engine.EnqueueCommandAsync(new PauseCommand());
            try
            {
                pauseResult = await pauseTask.WaitAsync(PauseTimeout);
                pauseLatency = probe.ElapsedMilliseconds(Stopwatch.GetTimestamp(), pauseStarted);
            }
            catch (TimeoutException)
            {
                pauseTimedOut = true;
            }

            var stopStarted = Stopwatch.GetTimestamp();
            var stopTask = engine.StopAsync();
            producerCancellation.Cancel();
            stopCompleted = await CompleteWithinAsync(stopTask, StopTimeout);
            if (stopCompleted)
            {
                stopLatency = probe.ElapsedMilliseconds(Stopwatch.GetTimestamp(), stopStarted);
            }

            await Task.WhenAll(producers).WaitAsync(StopTimeout);
            if (!pauseTask.IsCompleted)
            {
                pauseResult = await pauseTask.WaitAsync(StopTimeout);
            }

            return probe.CreateResult(
                "continuous-capacity-" + commandQueueCapacity,
                "continuous",
                commandQueueCapacity,
                producerCount,
                commandsPerProducer: 0,
                pauseResult,
                pauseTimedOut,
                pendingAtControlRequest,
                pauseLatency,
                stopLatency,
                stopCompleted,
                engine.EventJournal.TotalEventCount);
        }
        finally
        {
            producerCancellation.Cancel();
            tickCancellation.Cancel();
            await CompleteWithinAsync(Task.WhenAll(producers), StopTimeout);
            await tickObserver;
        }
    }

    private static FixedStepSimulationEngine CreateEngine(int commandQueueCapacity, LoadProbe probe) =>
        new(
            new SimulationSettings
            {
                CommandQueueCapacity = commandQueueCapacity,
                EventBufferCapacity = 256,
                CanonicalEventJournalCapacity = 200_000
            },
            point =>
            {
                if (point == SimulationEngineFaultPoint.BeforeCommandApplication)
                {
                    probe.ObserveApplication();
                }
            },
            _ => probe.ObserveAdmission());

    private static async Task PrepareEngineAsync(FixedStepSimulationEngine engine)
    {
        await engine.StartAsync();
        var channel = new ChannelDefinition
        {
            Id = "di.mch004.load",
            Name = "MCH-004 load input",
            Kind = ChannelKind.DigitalInput
        };
        var configuration = new SimulationRuntimeConfiguration(
            Array.Empty<AxisConfiguration>(),
            new[] { channel },
            Array.Empty<CompiledSequence>());
        var configure = await engine.EnqueueCommandAsync(new ConfigureRuntimeCommand(configuration));
        Assert.True(configure.IsAccepted, configure.Detail);
        var play = await engine.EnqueueCommandAsync(new PlayCommand());
        Assert.True(play.IsAccepted, play.Detail);
    }

    private static async Task ProduceFiniteAsync(
        FixedStepSimulationEngine engine,
        LoadProbe probe,
        int producerIndex,
        int commandCount,
        int batchSize)
    {
        for (var batchStart = 0; batchStart < commandCount; batchStart += batchSize)
        {
            var batch = Enumerable.Range(batchStart, Math.Min(batchSize, commandCount - batchStart))
                .Select(index => ProduceOneAsync(
                    engine,
                    probe,
                    (producerIndex + index) % 2 == 0,
                    CancellationToken.None))
                .ToArray();
            await Task.WhenAll(batch);
        }
    }

    private static async Task ProduceContinuouslyAsync(
        FixedStepSimulationEngine engine,
        LoadProbe probe,
        int producerIndex,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var index = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = Enumerable.Range(0, batchSize)
                .Select(_ => ProduceOneAsync(
                    engine,
                    probe,
                    (producerIndex + index++) % 2 == 0,
                    cancellationToken))
                .ToArray();
            await Task.WhenAll(batch);
        }
    }

    private static async Task ProduceOneAsync(
        FixedStepSimulationEngine engine,
        LoadProbe probe,
        bool value,
        CancellationToken cancellationToken)
    {
        var command = new SetVirtualInputCommand("di.mch004.load", value);
        probe.SubmittedCommand();
        try
        {
            var result = await engine.EnqueueCommandAsync(command, cancellationToken);
            probe.CompletedCommand(result);
        }
        catch (OperationCanceledException)
        {
            probe.CancelledCommand();
        }
        catch
        {
            probe.FaultedCommand();
            throw;
        }
    }

    private static async Task ObserveTicksAsync(
        FixedStepSimulationEngine engine,
        LoadProbe probe,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            probe.ObserveTick(engine.CurrentSnapshot.TickIndex);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<bool> CompleteWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string BuildMarkdown(LoadExperimentReport report)
    {
        var builder = new StringBuilder()
            .AppendLine("# MCH-004 command load experiment")
            .AppendLine()
            .AppendLine($"Observed at: `{report.ObservedAt:O}`")
            .AppendLine()
            .AppendLine("This is a measurement-only experiment. It records command submission/completion, " +
                "application observations, tick progression, Pause latency, Stop latency, and pending work " +
                "without changing production scheduling policy.")
            .AppendLine("Finite load uses 4 producers × 250 commands with a 16-command producer window. " +
                "Continuous load uses 16 producers with 32-command producer windows for 350 ms.")
            .AppendLine()
            .AppendLine("| Case | Producers | Queue | Submitted | Completed | Accepted | Rejected | Cancelled | Pending at control | " +
                "Admitted | Applied | Ticks | Max admission gap (ms) | Max application gap (ms) | " +
                "Max tick gap (ms) | Pause (ms) | Pause timed out | Stop (ms) | Events |")
            .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ");

        foreach (var result in report.Cases)
        {
            builder.Append("| ").Append(result.Name)
                .Append(" | ").Append(result.ProducerCount)
                .Append(" | ").Append(result.QueueCapacity)
                .Append(" | ").Append(result.Submitted)
                .Append(" | ").Append(result.Completed)
                .Append(" | ").Append(result.Accepted)
                .Append(" | ").Append(result.Rejected)
                .Append(" | ").Append(result.Cancelled)
                .Append(" | ").Append(result.PendingAtControlRequest)
                .Append(" | ").Append(result.AdmissionObservationCount)
                .Append(" | ").Append(result.ApplicationObservationCount)
                .Append(" | ").Append(result.LastTickIndex - result.FirstTickIndex)
                .Append(" | ").Append(FormatNullable(result.MaxAdmissionGapMilliseconds))
                .Append(" | ").Append(FormatNullable(result.MaxApplicationGapMilliseconds))
                .Append(" | ").Append(FormatNullable(result.MaxTickGapMilliseconds))
                .Append(" | ").Append(FormatNullable(result.PauseLatencyMilliseconds))
                .Append(" | ").Append(result.PauseTimedOut)
                .Append(" | ").Append(FormatNullable(result.StopLatencyMilliseconds))
                .Append(" | ").Append(result.EventCount)
                .AppendLine(" |");
        }

        builder.AppendLine()
            .AppendLine("Admission observations are emitted immediately after the bounded channel WriteAsync " +
                "succeeds. Application observations are emitted immediately before command application on " +
                "the engine reader. Both are internal measurement callbacks; no public admission event was added.");
        builder.AppendLine("Admitted/applied observation counts include the control Pause command; producer " +
            "submission/completion counts intentionally cover producer commands only.");
        return builder.ToString();
    }

    private static string FormatNullable(double? value) =>
        value?.ToString("F3", CultureInfo.InvariantCulture) ?? "n/a";

    private sealed record LoadExperimentReport(
        DateTimeOffset ObservedAt,
        IReadOnlyList<LoadCaseResult> Cases);

    private sealed record LoadCaseResult(
        string Name,
        string Mode,
        int QueueCapacity,
        int ProducerCount,
        int CommandsPerProducer,
        long Submitted,
        long Completed,
        long Accepted,
        long Rejected,
        long Cancelled,
        long Faulted,
        long PendingAtControlRequest,
        long PendingAfterStop,
        long FirstTickIndex,
        long LastTickIndex,
        long AdmissionObservationCount,
        long ApplicationObservationCount,
        double? MaxTickGapMilliseconds,
        double? MaxAdmissionGapMilliseconds,
        double? MaxApplicationGapMilliseconds,
        double? PauseLatencyMilliseconds,
        bool PauseTimedOut,
        double? StopLatencyMilliseconds,
        bool StopCompleted,
        string PauseOutcome,
        long EventCount);

    private sealed class LoadProbe
    {
        private long _recording;
        private long _submitted;
        private long _completed;
        private long _accepted;
        private long _rejected;
        private long _cancelled;
        private long _faulted;
        private long _pending;
        private long _admissionObservationCount;
        private long _lastAdmissionAt;
        private long _maxAdmissionGap;
        private long _applicationObservationCount;
        private long _lastApplicationAt;
        private long _maxApplicationGap;
        private long _lastTickIndex = -1;
        private long _firstTickIndex;
        private long _lastObservedTickAt;
        private long _maxTickGap;

        internal long Pending => Volatile.Read(ref _pending);

        internal void Begin()
        {
            Volatile.Write(ref _recording, 1);
        }

        internal void SubmittedCommand()
        {
            Interlocked.Increment(ref _submitted);
            Interlocked.Increment(ref _pending);
        }

        internal void CompletedCommand(SimulationCommandResult result)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _completed);
            if (result.IsAccepted)
            {
                Interlocked.Increment(ref _accepted);
            }
            else
            {
                Interlocked.Increment(ref _rejected);
            }
        }

        internal void CancelledCommand()
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _cancelled);
        }

        internal void FaultedCommand()
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _faulted);
        }

        internal void ObserveAdmission()
        {
            if (Volatile.Read(ref _recording) == 0)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _admissionObservationCount);
            var previous = Interlocked.Exchange(ref _lastAdmissionAt, now);
            if (previous > 0)
            {
                UpdateMaximum(ref _maxAdmissionGap, now - previous);
            }

        }

        internal void ObserveApplication()
        {
            if (Volatile.Read(ref _recording) == 0)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _applicationObservationCount);
            var previous = Interlocked.Exchange(ref _lastApplicationAt, now);
            if (previous > 0)
            {
                UpdateMaximum(ref _maxApplicationGap, now - previous);
            }

        }

        internal void ObserveTick(long tickIndex)
        {
            if (Volatile.Read(ref _recording) == 0)
            {
                return;
            }

            var previousTick = Interlocked.Exchange(ref _lastTickIndex, tickIndex);
            if (previousTick == tickIndex)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var previousObservation = Interlocked.Exchange(ref _lastObservedTickAt, now);
            if (previousObservation > 0)
            {
                UpdateMaximum(ref _maxTickGap, now - previousObservation);
            }

            if (previousTick < 0)
            {
                Interlocked.Exchange(ref _firstTickIndex, tickIndex);
            }
        }

        internal double ElapsedMilliseconds(long timestamp, long start)
        {
            return (timestamp - start) * 1000d / Stopwatch.Frequency;
        }

        internal LoadCaseResult CreateResult(
            string name,
            string mode,
            int queueCapacity,
            int producerCount,
            int commandsPerProducer,
            SimulationCommandResult? pauseResult,
            bool pauseTimedOut,
            long pendingAtControlRequest,
            double? pauseLatency,
            double? stopLatency,
            bool stopCompleted,
            long eventCount)
        {
            Volatile.Write(ref _recording, 0);
            var firstTickIndex = Volatile.Read(ref _firstTickIndex);
            var lastTickIndex = Volatile.Read(ref _lastTickIndex);
            return new LoadCaseResult(
                name,
                mode,
                queueCapacity,
                producerCount,
                commandsPerProducer,
                Volatile.Read(ref _submitted),
                Volatile.Read(ref _completed),
                Volatile.Read(ref _accepted),
                Volatile.Read(ref _rejected),
                Volatile.Read(ref _cancelled),
                Volatile.Read(ref _faulted),
                pendingAtControlRequest,
                Volatile.Read(ref _pending),
                firstTickIndex,
                lastTickIndex,
                Volatile.Read(ref _admissionObservationCount),
                Volatile.Read(ref _applicationObservationCount),
                ToMilliseconds(Volatile.Read(ref _maxTickGap)),
                ToMilliseconds(Volatile.Read(ref _maxAdmissionGap)),
                ToMilliseconds(Volatile.Read(ref _maxApplicationGap)),
                pauseLatency,
                pauseTimedOut,
                stopLatency,
                stopCompleted,
                pauseResult is null
                    ? "TimedOut"
                    : pauseResult.IsAccepted
                        ? "Accepted"
                        : pauseResult.ErrorCode.ToString(),
                eventCount);
        }

        private static void UpdateMaximum(ref long target, long candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref target, candidate, current) == current)
                {
                    return;
                }
            }
        }

        private static double? ToMilliseconds(long stopwatchTicks) =>
            stopwatchTicks > 0
                ? stopwatchTicks * 1000d / Stopwatch.Frequency
                : null;
    }
}
