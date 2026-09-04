using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class ScaleEvidenceTests
{
    private const string OutputEnvironmentVariable = "OPENVISIONLAB_SCALE_EVIDENCE_OUTPUT";
    private const int WarmupSampleCount = 1;
    private const int EvidenceSampleCount = 5;
    private const int RegressionSampleCount = 2;
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task AuditedScalePoints_CompileConfigureTickAndRemainDeterministic()
    {
        string? outputPath = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        int measuredSampleCount = string.IsNullOrWhiteSpace(outputPath)
            ? RegressionSampleCount
            : EvidenceSampleCount;
        ScaleCase[] cases =
        [
            new("devices-100", 100, 100, 101, CreateDeviceProject(100)),
            new("devices-500", 500, 500, 501, CreateDeviceProject(500)),
            new("io-1000", 0, 1_000, 0, CreateIoProject(1_000)),
            new("io-10000", 0, 10_000, 0, CreateIoProject(10_000))
        ];
        var results = new List<ScaleCaseResult>(cases.Length);

        foreach (ScaleCase scaleCase in cases)
        {
            for (int warmup = 0; warmup < WarmupSampleCount; warmup++)
            {
                await MeasureAsync(scaleCase);
            }

            var samples = new List<ScaleSample>(measuredSampleCount);
            for (int sampleIndex = 1; sampleIndex <= measuredSampleCount; sampleIndex++)
            {
                ScaleSample sample = await MeasureAsync(scaleCase) with { Sample = sampleIndex };
                Assert.Equal(scaleCase.ChannelCount, sample.SignalCount);
                Assert.Equal(scaleCase.LayoutComponentCount, sample.LayoutComponentCount);
                Assert.Equal(1, sample.TickIndex);
                samples.Add(sample);
            }

            Assert.Single(samples.Select(sample => sample.SnapshotSha256).Distinct(StringComparer.Ordinal));
            results.Add(new ScaleCaseResult(
                scaleCase.Name,
                scaleCase.DeviceCount,
                scaleCase.ChannelCount,
                scaleCase.LayoutComponentCount,
                samples[0].SnapshotSha256,
                Summarize(samples.Select(sample => sample.CompileElapsedMilliseconds)),
                Summarize(samples.Select(sample => sample.ConfigureElapsedMilliseconds)),
                Summarize(samples.Select(sample => sample.TickElapsedMilliseconds)),
                Summarize(samples.Select(sample => (double)sample.CompileAllocatedBytes)),
                Summarize(samples.Select(sample => (double)sample.ConfigureAllocatedBytes)),
                Summarize(samples.Select(sample => (double)sample.TickAllocatedBytes)),
                samples[0].SnapshotJsonBytes,
                samples));
        }

        var report = new ScaleEvidenceReport(
            1,
            DateTimeOffset.UtcNow,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            FixedStep.TotalMilliseconds,
            WarmupSampleCount,
            measuredSampleCount,
            "Elapsed values are wall-clock observations; allocation values are process-wide deltas.",
            results);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, JsonOptions));
        }
    }

    private static async Task<ScaleSample> MeasureAsync(ScaleCase scaleCase)
    {
        ForceCollection();
        long compileAllocationStart = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        MachineProjectRuntimeCompilationResult compilation =
            new MachineProjectRuntimeCompiler(FixedStep).Compile(scaleCase.Project);
        stopwatch.Stop();
        double compileElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        long compileAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - compileAllocationStart;
        Assert.True(
            compilation.IsSuccess,
            string.Join(Environment.NewLine, compilation.Errors.Select(error =>
                $"{error.Code}:{error.TargetId}:{error.Message}")));

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep
        });
        await engine.StartAsync();

        ForceCollection();
        long configureAllocationStart = GC.GetTotalAllocatedBytes(precise: true);
        stopwatch.Restart();
        SimulationCommandResult configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(compilation.Configuration!));
        stopwatch.Stop();
        double configureElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        long configureAllocatedBytes =
            GC.GetTotalAllocatedBytes(precise: true) - configureAllocationStart;
        Assert.True(configured.IsAccepted, configured.Detail);

        ForceCollection();
        long tickAllocationStart = GC.GetTotalAllocatedBytes(precise: true);
        stopwatch.Restart();
        SimulationCommandResult stepped = await engine.EnqueueCommandAsync(new StepCommand());
        stopwatch.Stop();
        double tickElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        long tickAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - tickAllocationStart;
        Assert.True(stepped.IsAccepted, stepped.Detail);

        SimulationSnapshot snapshot = engine.CurrentSnapshot;
        byte[] snapshotJson = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions);
        string snapshotSha256 = Convert.ToHexString(SHA256.HashData(snapshotJson)).ToLowerInvariant();
        await engine.StopAsync();

        return new ScaleSample(
            0,
            compileElapsedMilliseconds,
            configureElapsedMilliseconds,
            tickElapsedMilliseconds,
            CompileAllocatedBytes: compileAllocatedBytes,
            ConfigureAllocatedBytes: configureAllocatedBytes,
            TickAllocatedBytes: tickAllocatedBytes,
            SnapshotJsonBytes: snapshotJson.LongLength,
            SignalCount: snapshot.Signals.Count,
            LayoutComponentCount: snapshot.LayoutComponents.Count,
            TickIndex: snapshot.TickIndex,
            SnapshotSha256: snapshotSha256);
    }

    private static MachineProjectDocument CreateDeviceProject(int deviceCount)
    {
        var project = CreateProject($"scale-devices-{deviceCount}");
        project.Simulation.ActiveLayoutId = "scale-layout";
        var layout = new MachineLayoutDefinition
        {
            Id = "scale-layout",
            Name = "Scale Layout"
        };
        layout.Components.Add(new LayoutComponentDefinition
        {
            Id = "target-frame",
            Name = "Target Frame",
            Kind = LayoutComponentKind.MachineFrame,
            Transform = new Transform2D { X = 0, Y = 0 },
            Size = new Size2D { Width = 2_000, Height = 2_000 }
        });

        for (int index = 0; index < deviceCount; index++)
        {
            string suffix = index.ToString("D5");
            string deviceId = $"device.sensor.{suffix}";
            string componentId = $"sensor.{suffix}";
            string channelId = $"di.sensor.{suffix}";
            project.Channels.Add(new ChannelDefinition
            {
                Id = channelId,
                Name = $"Sensor {suffix}",
                Kind = ChannelKind.DigitalInput
            });
            project.Devices.Add(new DeviceDefinition
            {
                Id = deviceId,
                Name = $"Sensor {suffix}",
                Kind = DeviceKind.Sensor,
                ChannelIds = [channelId],
                Sensor = new DigitalSensorDefinition
                {
                    OutputChannelId = channelId,
                    TargetComponentId = "target-frame"
                }
            });
            layout.Components.Add(new LayoutComponentDefinition
            {
                Id = componentId,
                Name = $"Sensor {suffix}",
                Kind = LayoutComponentKind.DigitalSensor,
                BehaviorBindingId = deviceId,
                Transform = new Transform2D
                {
                    X = (index % 25) * 40,
                    Y = (index / 25) * 40
                },
                Size = new Size2D { Width = 20, Height = 20 },
                ZIndex = 1
            });
        }

        project.Layouts.Add(layout);
        return project;
    }

    private static MachineProjectDocument CreateIoProject(int channelCount)
    {
        var project = CreateProject($"scale-io-{channelCount}");
        for (int index = 0; index < channelCount; index++)
        {
            string suffix = index.ToString("D5");
            project.Channels.Add(new ChannelDefinition
            {
                Id = $"{(index % 2 == 0 ? "di" : "do")}.scale.{suffix}",
                Name = $"Scale I/O {suffix}",
                Kind = index % 2 == 0 ? ChannelKind.DigitalInput : ChannelKind.DigitalOutput
            });
        }

        return project;
    }

    private static MachineProjectDocument CreateProject(string id) => new()
    {
        Id = id,
        Name = id,
        CreatedAt = DateTimeOffset.UnixEpoch,
        ModifiedAt = DateTimeOffset.UnixEpoch,
        Simulation = new SimulationDefinition
        {
            FixedStepMilliseconds = 5,
            DefaultTimeScale = 1
        }
    };

    private static MetricSummary Summarize(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        double median = ordered.Length % 2 == 0
            ? (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2
            : ordered[ordered.Length / 2];
        int p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
        return new MetricSummary(ordered[0], median, ordered.Average(), ordered[p95Index], ordered[^1]);
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record ScaleCase(
        string Name,
        int DeviceCount,
        int ChannelCount,
        int LayoutComponentCount,
        MachineProjectDocument Project);

    private sealed record ScaleEvidenceReport(
        int SchemaVersion,
        DateTimeOffset GeneratedAtUtc,
        string Framework,
        string OperatingSystem,
        string ProcessArchitecture,
        int ProcessorCount,
        bool ServerGc,
        double FixedStepMilliseconds,
        int WarmupSampleCount,
        int MeasuredSampleCount,
        string MeasurementBoundary,
        IReadOnlyList<ScaleCaseResult> Cases);

    private sealed record ScaleCaseResult(
        string Name,
        int DeviceCount,
        int ChannelCount,
        int LayoutComponentCount,
        string SnapshotSha256,
        MetricSummary CompileElapsedMilliseconds,
        MetricSummary ConfigureElapsedMilliseconds,
        MetricSummary TickElapsedMilliseconds,
        MetricSummary CompileAllocatedBytes,
        MetricSummary ConfigureAllocatedBytes,
        MetricSummary TickAllocatedBytes,
        long SnapshotJsonBytes,
        IReadOnlyList<ScaleSample> Samples);

    private sealed record ScaleSample(
        int Sample,
        double CompileElapsedMilliseconds,
        double ConfigureElapsedMilliseconds,
        double TickElapsedMilliseconds,
        long CompileAllocatedBytes,
        long ConfigureAllocatedBytes,
        long TickAllocatedBytes,
        long SnapshotJsonBytes,
        int SignalCount,
        int LayoutComponentCount,
        long TickIndex,
        string SnapshotSha256);

    private sealed record MetricSummary(double Min, double Median, double Mean, double P95, double Max);
}
