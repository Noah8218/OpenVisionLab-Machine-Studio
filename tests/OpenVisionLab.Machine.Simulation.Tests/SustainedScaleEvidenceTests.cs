using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class SustainedScaleEvidenceTests
{
    private const string OutputEnvironmentVariable = "OPENVISIONLAB_SUSTAINED_SCALE_EVIDENCE_OUTPUT";
    private const string DurationEnvironmentVariable = "OPENVISIONLAB_SUSTAINED_SCALE_DURATION_SECONDS";
    private const int ChannelCount = 10_000;
    private const int EvidenceDurationSeconds = 60;
    private const int RegressionDurationSeconds = 2;
    private const int WarmupSeconds = 2;
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task TenThousandIo_RealTimeSnapshotDeliveryRecordsSustainedGcEvidence()
    {
        string? outputPath = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        int durationSeconds = ResolveDurationSeconds(outputPath);
        SustainedScaleReport report = await RunAsync(durationSeconds);

        Assert.True(report.MeasuredWallMilliseconds >= durationSeconds * 1_000);
        Assert.Equal(ChannelCount, report.FinalSignalCount);
        Assert.True(report.TickCount > 0);
        Assert.True(report.DeliveredTickSnapshots > 0);
        Assert.Equal(0, report.InvalidSignalSnapshotCount);
        Assert.Equal(0, report.MonotonicTickViolationCount);
        Assert.NotEmpty(report.FinalSignalStateSha256);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, JsonOptions));
        }
    }

    private static async Task<SustainedScaleReport> RunAsync(int durationSeconds)
    {
        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep
        });
        await engine.StartAsync();
        SimulationCommandResult configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(CreateRuntimeConfiguration()));
        Assert.True(configured.IsAccepted, configured.Detail);

        var consumerState = new SnapshotConsumerState(ChannelCount);
        Task consumer = ConsumeSnapshotsAsync(engine.SnapshotReader, consumerState);
        SimulationCommandResult playing = await engine.EnqueueCommandAsync(new PlayCommand());
        Assert.True(playing.IsAccepted, playing.Detail);
        await Task.Delay(TimeSpan.FromSeconds(WarmupSeconds));

        SimulationCommandResult warmupPaused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(warmupPaused.IsAccepted, warmupPaused.Detail);
        SimulationSnapshot startSnapshot = engine.CurrentSnapshot;
        await WaitForConsumerTickAsync(consumerState, startSnapshot.TickIndex);
        consumerState.BeginMeasurement(startSnapshot.TickIndex);

        SimulationCommandResult measurementPlaying = await engine.EnqueueCommandAsync(new PlayCommand());
        Assert.True(measurementPlaying.IsAccepted, measurementPlaying.Detail);

        Process process = Process.GetCurrentProcess();
        RuntimeCounters start = CaptureCounters(process);
        ConsumerCounters startConsumer = consumerState.Capture();
        long startTick = startSnapshot.TickIndex;
        var samples = new List<SustainedScaleSample>(durationSeconds + 1);
        var measurement = Stopwatch.StartNew();

        for (int second = 1; second <= durationSeconds; second++)
        {
            TimeSpan delay = TimeSpan.FromSeconds(second) - measurement.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            samples.Add(CreateSample(
                measurement.Elapsed,
                engine.CurrentSnapshot.TickIndex - startTick,
                start,
                CaptureCounters(process),
                startConsumer,
                consumerState.Capture()));
        }

        SimulationCommandResult paused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(paused.IsAccepted, paused.Detail);
        measurement.Stop();
        long finalTick = engine.CurrentSnapshot.TickIndex;
        RuntimeCounters end = CaptureCounters(process);
        await engine.StopAsync();
        await consumer;
        ConsumerCounters endConsumer = consumerState.Capture();
        SimulationSnapshot finalSnapshot = engine.CurrentSnapshot;

        TimeSpan wall = measurement.Elapsed;
        long tickCount = finalTick - startTick;
        long delivered = endConsumer.DeliveredTickSnapshots - startConsumer.DeliveredTickSnapshots;
        long skipped = endConsumer.SkippedTickSnapshots - startConsumer.SkippedTickSnapshots;
        long allocatedBytes = end.TotalAllocatedBytes - start.TotalAllocatedBytes;
        TimeSpan gcPause = end.TotalGcPause - start.TotalGcPause;
        TimeSpan cpu = end.TotalProcessorTime - start.TotalProcessorTime;
        int invalidSignalSnapshotCount =
            endConsumer.InvalidSignalSnapshotCount - startConsumer.InvalidSignalSnapshotCount;
        int monotonicTickViolationCount =
            endConsumer.MonotonicTickViolationCount - startConsumer.MonotonicTickViolationCount;
        long targetTickCount = (long)Math.Round(wall.TotalMilliseconds / FixedStep.TotalMilliseconds);
        byte[] finalSignalJson = JsonSerializer.SerializeToUtf8Bytes(
            finalSnapshot.Signals,
            HashJsonOptions);

        return new SustainedScaleReport(
            1,
            DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            ChannelCount,
            startSnapshot.SignalRevision,
            finalSnapshot.SignalRevision,
            FixedStep.TotalMilliseconds,
            WarmupSeconds,
            durationSeconds,
            wall.TotalMilliseconds,
            targetTickCount,
            tickCount,
            tickCount / wall.TotalSeconds,
            tickCount - targetTickCount,
            delivered,
            skipped,
            delivered + skipped == 0 ? 0 : skipped * 100d / (delivered + skipped),
            allocatedBytes,
            allocatedBytes / wall.TotalSeconds,
            end.Gen0Collections - start.Gen0Collections,
            end.Gen1Collections - start.Gen1Collections,
            end.Gen2Collections - start.Gen2Collections,
            gcPause.TotalMilliseconds,
            gcPause.TotalMilliseconds * 100d / wall.TotalMilliseconds,
            start.ManagedBytes,
            end.ManagedBytes,
            Math.Max(
                Math.Max(start.ManagedBytes, end.ManagedBytes),
                samples.Max(sample => sample.ManagedBytes)),
            start.GcHeapBytes,
            end.GcHeapBytes,
            Math.Max(
                Math.Max(start.GcHeapBytes, end.GcHeapBytes),
                samples.Max(sample => sample.GcHeapBytes)),
            start.WorkingSetBytes,
            end.WorkingSetBytes,
            Math.Max(
                Math.Max(start.WorkingSetBytes, end.WorkingSetBytes),
                samples.Max(sample => sample.WorkingSetBytes)),
            start.PrivateMemoryBytes,
            end.PrivateMemoryBytes,
            Math.Max(
                Math.Max(start.PrivateMemoryBytes, end.PrivateMemoryBytes),
                samples.Max(sample => sample.PrivateMemoryBytes)),
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds * 100d / wall.TotalMilliseconds,
            cpu.TotalMilliseconds * 100d / wall.TotalMilliseconds / Environment.ProcessorCount,
            finalSnapshot.Signals.Count,
            Convert.ToHexString(SHA256.HashData(finalSignalJson)).ToLowerInvariant(),
            invalidSignalSnapshotCount,
            monotonicTickViolationCount,
            endConsumer.LastSignalChecksum,
            "Process-wide allocation/GC and process memory/CPU deltas from an isolated test process; latest-snapshot delivery intentionally drops old snapshots when the active consumer falls behind.",
            samples);
    }

    private static SimulationRuntimeConfiguration CreateRuntimeConfiguration()
    {
        var channels = new ChannelDefinition[ChannelCount];
        for (int index = 0; index < channels.Length; index++)
        {
            string suffix = index.ToString("D5");
            channels[index] = new ChannelDefinition
            {
                Id = $"{(index % 2 == 0 ? "di" : "do")}.sustained.{suffix}",
                Name = $"Sustained I/O {suffix}",
                Kind = index % 2 == 0 ? ChannelKind.DigitalInput : ChannelKind.DigitalOutput
            };
        }

        return new SimulationRuntimeConfiguration(
            Array.Empty<Axis.AxisConfiguration>(),
            channels,
            Array.Empty<CompiledSequence>());
    }

    private static async Task ConsumeSnapshotsAsync(
        System.Threading.Channels.ChannelReader<SimulationSnapshot> reader,
        SnapshotConsumerState state)
    {
        await foreach (SimulationSnapshot snapshot in reader.ReadAllAsync())
        {
            state.Consume(snapshot);
        }
    }

    private static async Task WaitForConsumerTickAsync(SnapshotConsumerState state, long tickIndex)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (state.Capture().LastTickIndex < tickIndex)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static SustainedScaleSample CreateSample(
        TimeSpan elapsed,
        long tickCount,
        RuntimeCounters start,
        RuntimeCounters current,
        ConsumerCounters startConsumer,
        ConsumerCounters currentConsumer) =>
        new(
            elapsed.TotalMilliseconds,
            tickCount,
            tickCount / elapsed.TotalSeconds,
            currentConsumer.DeliveredTickSnapshots - startConsumer.DeliveredTickSnapshots,
            currentConsumer.SkippedTickSnapshots - startConsumer.SkippedTickSnapshots,
            current.TotalAllocatedBytes - start.TotalAllocatedBytes,
            current.Gen0Collections - start.Gen0Collections,
            current.Gen1Collections - start.Gen1Collections,
            current.Gen2Collections - start.Gen2Collections,
            (current.TotalGcPause - start.TotalGcPause).TotalMilliseconds,
            current.ManagedBytes,
            current.GcHeapBytes,
            current.WorkingSetBytes,
            current.PrivateMemoryBytes,
            (current.TotalProcessorTime - start.TotalProcessorTime).TotalMilliseconds);

    private static RuntimeCounters CaptureCounters(Process process)
    {
        process.Refresh();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new RuntimeCounters(
            GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration(),
            GC.GetTotalMemory(forceFullCollection: false),
            gc.HeapSizeBytes,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.TotalProcessorTime);
    }

    private static int ResolveDurationSeconds(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return RegressionDurationSeconds;
        }

        string? value = Environment.GetEnvironmentVariable(DurationEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return EvidenceDurationSeconds;
        }

        if (!int.TryParse(value, out int durationSeconds) || durationSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{DurationEnvironmentVariable} must be a positive integer.");
        }

        return durationSeconds;
    }

    private sealed class SnapshotConsumerState
    {
        private readonly object _sync = new();
        private readonly int _expectedSignalCount;
        private long _lastTickIndex = -1;
        private long _deliveredTickSnapshots;
        private long _skippedTickSnapshots;
        private int _invalidSignalSnapshotCount;
        private int _monotonicTickViolationCount;
        private long _lastSignalChecksum;

        public SnapshotConsumerState(int expectedSignalCount)
        {
            _expectedSignalCount = expectedSignalCount;
        }

        public void BeginMeasurement(long tickIndex)
        {
            lock (_sync)
            {
                _lastTickIndex = tickIndex;
                _deliveredTickSnapshots = 0;
                _skippedTickSnapshots = 0;
                _invalidSignalSnapshotCount = 0;
                _monotonicTickViolationCount = 0;
            }
        }

        public void Consume(SimulationSnapshot snapshot)
        {
            long checksum = 17;
            foreach (var signal in snapshot.Signals)
            {
                checksum = unchecked((checksum * 31) + signal.Id.Length);
                checksum = unchecked((checksum * 31) + (signal.Value ? 1 : 0));
            }

            lock (_sync)
            {
                if (snapshot.Signals.Count != _expectedSignalCount)
                {
                    _invalidSignalSnapshotCount++;
                }

                if (_lastTickIndex < 0)
                {
                    _lastTickIndex = snapshot.TickIndex;
                }
                else if (snapshot.TickIndex > _lastTickIndex)
                {
                    _deliveredTickSnapshots++;
                    _skippedTickSnapshots += snapshot.TickIndex - _lastTickIndex - 1;
                    _lastTickIndex = snapshot.TickIndex;
                }
                else if (snapshot.TickIndex < _lastTickIndex)
                {
                    _monotonicTickViolationCount++;
                }

                _lastSignalChecksum = checksum;
            }
        }

        public ConsumerCounters Capture()
        {
            lock (_sync)
            {
                return new ConsumerCounters(
                    _lastTickIndex,
                    _deliveredTickSnapshots,
                    _skippedTickSnapshots,
                    _invalidSignalSnapshotCount,
                    _monotonicTickViolationCount,
                    _lastSignalChecksum);
            }
        }
    }

    private sealed record RuntimeCounters(
        long TotalAllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        TimeSpan TotalGcPause,
        long ManagedBytes,
        long GcHeapBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        TimeSpan TotalProcessorTime);

    private sealed record ConsumerCounters(
        long LastTickIndex,
        long DeliveredTickSnapshots,
        long SkippedTickSnapshots,
        int InvalidSignalSnapshotCount,
        int MonotonicTickViolationCount,
        long LastSignalChecksum);

    private sealed record SustainedScaleReport(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string Framework,
        string OperatingSystem,
        string ProcessArchitecture,
        int ProcessorCount,
        bool ServerGc,
        int ChannelCount,
        long SignalRevisionStart,
        long SignalRevisionEnd,
        double FixedStepMilliseconds,
        int WarmupSeconds,
        int RequestedDurationSeconds,
        double MeasuredWallMilliseconds,
        long TargetTickCount,
        long TickCount,
        double TickRatePerSecond,
        long TickDrift,
        long DeliveredTickSnapshots,
        long SkippedTickSnapshots,
        double SkippedTickSnapshotPercent,
        long AllocatedBytes,
        double AllocatedBytesPerSecond,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double GcPauseMilliseconds,
        double GcPausePercent,
        long ManagedBytesStart,
        long ManagedBytesEnd,
        long ManagedBytesMaximum,
        long GcHeapBytesStart,
        long GcHeapBytesEnd,
        long GcHeapBytesMaximum,
        long WorkingSetBytesStart,
        long WorkingSetBytesEnd,
        long WorkingSetBytesMaximum,
        long PrivateMemoryBytesStart,
        long PrivateMemoryBytesEnd,
        long PrivateMemoryBytesMaximum,
        double ProcessorTimeMilliseconds,
        double ProcessCpuEquivalentLogicalCorePercent,
        double ProcessCpuPercentOfMachineCapacity,
        int FinalSignalCount,
        string FinalSignalStateSha256,
        int InvalidSignalSnapshotCount,
        int MonotonicTickViolationCount,
        long LastSignalChecksum,
        string MeasurementBoundary,
        IReadOnlyList<SustainedScaleSample> Samples);

    private sealed record SustainedScaleSample(
        double ElapsedMilliseconds,
        long TickCount,
        double TickRatePerSecond,
        long DeliveredTickSnapshots,
        long SkippedTickSnapshots,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double GcPauseMilliseconds,
        long ManagedBytes,
        long GcHeapBytes,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        double ProcessorTimeMilliseconds);
}
