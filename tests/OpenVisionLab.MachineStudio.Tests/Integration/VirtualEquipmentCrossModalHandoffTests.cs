using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Sequence.Compilation;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Events;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests.Integration;

public sealed class VirtualEquipmentCrossModalHandoffTests
{
    private const string CameraId = "camera.virtual";
    private const string RecipeId = "inspection-cross-modal";
    private const string AcquisitionId = "camera.virtual/frame/00000001";
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task DeterministicCameraFrame_PublishesCorrelatedTwoDAndThreeDHandoffsWithoutAutoRun()
    {
        using var fixture = new Fixture();
        using var engine = new FixedStepSimulationEngine(new SimulationSettings { FixedStep = FixedStep });

        await engine.StartAsync();
        var configured = await engine.EnqueueCommandAsync(
            new ConfigureRuntimeCommand(
                new SimulationRuntimeConfiguration(
                    Array.Empty<AxisConfiguration>(),
                    Array.Empty<ChannelDefinition>(),
                    Array.Empty<CompiledSequence>(),
                    new[]
                    {
                        new VirtualCameraConfiguration(
                            CameraId,
                            "Virtual inspection camera",
                            exposureTicks: 2,
                            transferTicks: 3,
                            PlaceholderInspectionDecision.Pass)
                    })));
        Assert.True(configured.IsAccepted, configured.Detail);
        Assert.True((await engine.EnqueueCommandAsync(new StartManualControlCommand())).IsAccepted);
        Assert.True((await engine.EnqueueCommandAsync(new PauseCommand())).IsAccepted);

        var frameContent = File.ReadAllBytes(fixture.ImagePath);
        var frame = new VirtualCameraFrameEvidence(
            AcquisitionId,
            "artifacts/frame.png",
            Convert.ToHexString(SHA256.HashData(frameContent)),
            frameContent.LongLength,
            4,
            4,
            "Mono8");
        var trigger = await engine.EnqueueCommandAsync(
            new TriggerVirtualCameraCommand(CameraId, RecipeId, frame));
        Assert.True(trigger.IsAccepted, trigger.Detail);

        for (var index = 0; index < 5; index++)
        {
            var step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, step.Detail);
        }

        var camera = Assert.Single(engine.CurrentSnapshot.Cameras);
        var acquisition = Assert.IsType<VirtualCameraAcquisitionResult>(camera.Result);
        Assert.Equal(VirtualCameraState.FrameReady, camera.State);
        Assert.Equal(AcquisitionId, acquisition.AcquisitionId);
        Assert.Equal(frame, acquisition.FrameEvidence);

        await engine.StopAsync();
        var events = await ReadAllEventsAsync(engine);
        var triggered = Assert.Single(events, item => item.Code == "CameraTriggered");
        var exposureCompleted = Assert.Single(events, item => item.Code == "CameraExposureCompleted");
        var frameReady = Assert.Single(events, item => item.Code == "CameraFrameReady");
        Assert.True(triggered.EventIndex < exposureCompleted.EventIndex);
        Assert.True(exposureCompleted.EventIndex < frameReady.EventIndex);
        Assert.Contains(AcquisitionId, frameReady.Message, StringComparison.Ordinal);

        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "0.2.0-dev.37",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var twoDConsumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        var threeDConsumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.ThreeDStudio,
            "0.2.0-alpha.1",
            new string('3', 40),
            IntegrationSourceState.Clean);

        var twoDHandoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                fixture.TwoDRecipePath,
                fixture.ImagePath,
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                producer,
                twoDConsumer));
        var threeDHandoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                fixture.ThreeDRecipePath,
                fixture.HeightMapPath,
                IntegrationInspectionModality.ThreeD,
                IntegrationInspectionInputKind.HeightMap,
                producer,
                threeDConsumer));

        var persistedTwoD = MachineIntegrationExchange.ReadHandoff(
            fixture.ExchangeRoot,
            twoDHandoff.TransactionId);
        var persistedThreeD = MachineIntegrationExchange.ReadHandoff(
            fixture.ExchangeRoot,
            threeDHandoff.TransactionId);
        Assert.Equal(IntegrationInspectionModality.TwoD, persistedTwoD.Context.Modality);
        Assert.Equal(IntegrationInspectionInputKind.Image, persistedTwoD.Context.InputKind);
        Assert.Equal(IntegrationApplicationIds.TwoDStudio, persistedTwoD.Context.ConsumerBuild.ApplicationId);
        Assert.Equal(IntegrationInspectionModality.ThreeD, persistedThreeD.Context.Modality);
        Assert.Equal(IntegrationInspectionInputKind.HeightMap, persistedThreeD.Context.InputKind);
        Assert.Equal(IntegrationApplicationIds.ThreeDStudio, persistedThreeD.Context.ConsumerBuild.ApplicationId);
        Assert.Equal(persistedTwoD.Context.ProjectId, persistedThreeD.Context.ProjectId);
        Assert.Equal(persistedTwoD.Context.SequenceId, persistedThreeD.Context.SequenceId);
        Assert.Equal(persistedTwoD.Context.StepId, persistedThreeD.Context.StepId);
        Assert.Equal(persistedTwoD.Context.CameraId, persistedThreeD.Context.CameraId);
        Assert.Equal(persistedTwoD.Context.AcquisitionId, persistedThreeD.Context.AcquisitionId);
        Assert.Equal(persistedTwoD.Context.FrameId, persistedThreeD.Context.FrameId);

        AssertArtifactMatches(
            fixture.ExchangeRoot,
            twoDHandoff,
            persistedTwoD,
            IntegrationArtifactRoles.InspectionSource,
            fixture.ImagePath);
        AssertArtifactMatches(
            fixture.ExchangeRoot,
            threeDHandoff,
            persistedThreeD,
            IntegrationArtifactRoles.InspectionSource,
            fixture.HeightMapPath);

        var transactions = MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot);
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, transaction =>
        {
            Assert.False(transaction.HasAcknowledgement);
            Assert.False(transaction.HasResult);
        });
        AssertNoConsumerFiles(fixture.ExchangeRoot, twoDHandoff.TransactionId);
        AssertNoConsumerFiles(fixture.ExchangeRoot, threeDHandoff.TransactionId);
    }

    private static void AssertArtifactMatches(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IntegrationHandoffV2 persisted,
        string role,
        string sourcePath)
    {
        var artifact = Assert.Single(persisted.Context.Artifacts.Where(item => item.Role == role));
        var copiedPath = Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"),
            artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(copiedPath));
        Assert.Equal(new FileInfo(sourcePath).Length, artifact.ByteLength);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
            artifact.Sha256);
        Assert.Equal(artifact.ByteLength, new FileInfo(copiedPath).Length);
        Assert.Equal(
            artifact.Sha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copiedPath))));
    }

    private static void AssertNoConsumerFiles(string exchangeRoot, Guid transactionId)
    {
        var transactionRoot = Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            transactionId.ToString("D"));
        Assert.False(
            File.Exists(
                Path.Combine(transactionRoot, IntegrationTransactionLayout.AcknowledgementFileName)));
        Assert.False(
            File.Exists(
                Path.Combine(transactionRoot, IntegrationTransactionLayout.ResultFileName)));
    }

    private static async Task<IReadOnlyList<SimulationEvent>> ReadAllEventsAsync(
        FixedStepSimulationEngine engine)
    {
        var events = new List<SimulationEvent>();
        await foreach (var item in engine.EventReader.ReadAllAsync())
        {
            events.Add(item);
        }

        return events;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                TestStorage.RootPath,
                "p2-2d3d-virtual-equipment-contract-audit-20260912",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            ExchangeRoot = Path.Combine(Root, "exchange");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(ExchangeRoot);

            MachineProjectPath = Path.Combine(SourceRoot, "machine.ovmachine");
            TwoDRecipePath = Path.Combine(SourceRoot, "inspection-2d.json");
            ThreeDRecipePath = Path.Combine(SourceRoot, "inspection-3d.json");
            ImagePath = Path.Combine(SourceRoot, "frame.png");
            HeightMapPath = Path.Combine(SourceRoot, "height-map.c3d");
            File.WriteAllText(
                MachineProjectPath,
                "{\"schema\":\"machine-project/1.0\"}",
                new UTF8Encoding(false));
            File.WriteAllText(
                TwoDRecipePath,
                "{\"schema\":\"vision-pipeline/1.0\"}",
                new UTF8Encoding(false));
            File.WriteAllText(
                ThreeDRecipePath,
                "{\"schema\":\"height-pipeline/1.0\"}",
                new UTF8Encoding(false));
            File.WriteAllBytes(ImagePath, [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03, 0x04]);
            File.WriteAllBytes(HeightMapPath, [0x43, 0x33, 0x44, 0x01, 0x02, 0x03, 0x04, 0x05]);
        }

        public string Root { get; }
        private string SourceRoot { get; }
        public string ExchangeRoot { get; }
        public string MachineProjectPath { get; }
        public string TwoDRecipePath { get; }
        public string ThreeDRecipePath { get; }
        public string ImagePath { get; }
        public string HeightMapPath { get; }

        public MachineInspectionHandoffRequest CreateRequest(
            string recipePath,
            string sourcePath,
            IntegrationInspectionModality modality,
            IntegrationInspectionInputKind inputKind,
            IntegrationApplicationIdentity producer,
            IntegrationApplicationIdentity consumer) =>
            new(
                "machine-project",
                "machine-project/1.0",
                "sequence-virtual-equipment",
                "inspect-cross-modal",
                CameraId,
                AcquisitionId,
                AcquisitionId,
                "mm",
                MachineProjectPath,
                sourcePath,
                recipePath,
                modality,
                inputKind,
                producer,
                consumer);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
