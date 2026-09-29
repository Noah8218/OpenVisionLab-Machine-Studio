using System.Text;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class Mch032CustomerNeutralAdapterContractTests
{
    [Theory]
    [InlineData(
        IntegrationInspectionModality.TwoD,
        IntegrationInspectionInputKind.Image,
        IntegrationApplicationIds.TwoDStudio,
        "artifacts/inspection-source.png")]
    [InlineData(
        IntegrationInspectionModality.ThreeD,
        IntegrationInspectionInputKind.HeightMap,
        IntegrationApplicationIds.ThreeDStudio,
        "artifacts/inspection-source.c3d")]
    public async Task SupportedShape_MapsToNeutralRequestWithoutAutoRun(
        IntegrationInspectionModality modality,
        IntegrationInspectionInputKind inputKind,
        string consumerApplicationId,
        string expectedSourcePath)
    {
        using var fixture = new Fixture();
        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                modality,
                inputKind,
                fixture.CreateConsumer(consumerApplicationId)));

        var validation = IntegrationContractValidator.Validate(handoff);
        Assert.True(
            validation.IsValid,
            string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue =>
                    $"{issue.Code}: {issue.Field}: {issue.Message}")));

        var request = CustomerNeutralAdapterContract.Interpret(handoff);
        Assert.Equal(handoff.TransactionId, request.TransactionId);
        Assert.Equal(handoff.Context.ProjectId, request.ProjectId);
        Assert.Equal(handoff.Context.SequenceId, request.SequenceId);
        Assert.Equal(handoff.Context.StepId, request.StepId);
        Assert.Equal(handoff.Context.CameraId, request.CameraId);
        Assert.Equal(handoff.Context.AcquisitionId, request.AcquisitionId);
        Assert.Equal(handoff.Context.FrameId, request.FrameId);
        Assert.Equal(modality, request.Modality);
        Assert.Equal(inputKind, request.InputKind);
        Assert.Equal(consumerApplicationId, request.ConsumerBuild.ApplicationId);
        Assert.Equal(expectedSourcePath, request.InputArtifact.RelativePath);
        Assert.Equal(
            IntegrationArtifactRoles.InspectionRecipe,
            request.RecipeArtifact.Role);
        Assert.True(request.RequiresExplicitExecutionApproval);

        var transaction = Assert.Single(
            MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
        Assert.False(transaction.HasAcknowledgement);
        Assert.False(transaction.HasResult);
    }

    [Theory]
    [InlineData(
        "com.customer.inspector",
        IntegrationInspectionModality.TwoD,
        IntegrationInspectionInputKind.Image)]
    [InlineData(
        IntegrationApplicationIds.ThreeDStudio,
        IntegrationInspectionModality.TwoD,
        IntegrationInspectionInputKind.Image)]
    [InlineData(
        IntegrationApplicationIds.TwoDStudio,
        IntegrationInspectionModality.TwoD,
        IntegrationInspectionInputKind.HeightMap)]
    [InlineData(
        IntegrationApplicationIds.TwoDStudio,
        IntegrationInspectionModality.Ai,
        IntegrationInspectionInputKind.ExternalEvidence)]
    public async Task UnsupportedApplicationOrShape_IsRejectedBeforePublication(
        string consumerApplicationId,
        IntegrationInspectionModality modality,
        IntegrationInspectionInputKind inputKind)
    {
        using var fixture = new Fixture();
        var request = fixture.CreateRequest(
            modality,
            inputKind,
            fixture.CreateConsumer(consumerApplicationId));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            MachineIntegrationHandoffPublisher.PublishAsync(
                fixture.ExchangeRoot,
                request));

        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
    }

    [Fact]
    public async Task AcknowledgementProducerMustMatchExactConsumerBuild()
    {
        using var fixture = new Fixture();
        var consumer = fixture.CreateConsumer(IntegrationApplicationIds.TwoDStudio);
        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                consumer));

        foreach (var mismatchedConsumer in new[]
        {
            consumer with { ApplicationVersion = "2.1.1" },
            consumer with { SourceCommit = new string('9', 40) },
            consumer with { SourceState = IntegrationSourceState.Dirty },
            consumer with { SourceCommit = new string('9', 39) }
        })
        {
            var acknowledgement = CreateAcknowledgement(
                handoff,
                mismatchedConsumer,
                IntegrationAcknowledgementStatus.Accepted);
            var validation = IntegrationContractValidator.ValidateV2Sequence(
                handoff,
                acknowledgement);

            Assert.Contains(
                validation.Issues,
                issue => issue.Code == IntegrationErrorCode.CorrelationMismatch
                    && issue.Field == "acknowledgement.producer");
        }

        var malformedHandoff = handoff with
        {
            Producer = handoff.Producer with
            {
                SourceCommit = new string('X', 40)
            }
        };
        var malformedValidation = IntegrationContractValidator.Validate(malformedHandoff);
        Assert.Contains(
            malformedValidation.Issues,
            issue => issue.Code == IntegrationErrorCode.InvalidIdentity
                && issue.Field == "producer");
    }

    [Fact]
    public async Task RequiredContextAndArtifactIdentity_FailClosedBeforeMapping()
    {
        using var fixture = new Fixture();
        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                fixture.CreateConsumer(IntegrationApplicationIds.TwoDStudio)));

        var missingProject = handoff with
        {
            Context = handoff.Context with { ProjectId = string.Empty }
        };
        var missingProjectValidation = IntegrationContractValidator.Validate(missingProject);
        Assert.Contains(
            missingProjectValidation.Issues,
            issue => issue.Code == IntegrationErrorCode.InvalidIdentity
                && issue.Field == "context.projectId");

        var missingArtifacts = handoff with
        {
            Context = handoff.Context with { Artifacts = Array.Empty<IntegrationArtifactReference>() }
        };
        var missingArtifactsValidation = IntegrationContractValidator.Validate(missingArtifacts);
        Assert.Contains(
            missingArtifactsValidation.Issues,
            issue => issue.Code == IntegrationErrorCode.InvalidArtifact
                && issue.Field == "context.artifacts");

        var changedInputHash = handoff with
        {
            Context = handoff.Context with
            {
                Artifacts = handoff.Context.Artifacts
                    .Select(artifact => artifact.Role == IntegrationArtifactRoles.InspectionSource
                        ? artifact with { Sha256 = new string('F', 64) }
                        : artifact)
                    .ToArray()
            }
        };
        var exception = Assert.Throws<IntegrationContractException>(() =>
            CustomerNeutralAdapterContract.Interpret(changedInputHash));
        Assert.Equal(IntegrationErrorCode.InvalidArtifact, exception.ErrorCode);
    }

    [Fact]
    public async Task AcceptedAcknowledgementAndTerminalResultDefineExplicitOutputSequence()
    {
        using var fixture = new Fixture();
        var consumer = fixture.CreateConsumer(IntegrationApplicationIds.TwoDStudio);
        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                consumer));
        var accepted = CreateAcknowledgement(
            handoff,
            consumer,
            IntegrationAcknowledgementStatus.Accepted);
        var acceptedValidation = IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            accepted);
        Assert.True(acceptedValidation.IsValid);

        var result = new IntegrationResultV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Result,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            accepted.MessageId,
            accepted.CreatedAtUtc.AddMilliseconds(1),
            consumer,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass,
            "run-032",
            new IntegrationArtifactReference(
                IntegrationArtifactRoles.RunRecord,
                "run-032",
                "artifacts/run-record.json",
                1,
                new string('A', 64)),
            IntegrationRunCorrelation.FromContext(handoff.Context),
            [],
            [],
            null);
        var sequenceValidation = IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            accepted,
            result);
        Assert.True(
            sequenceValidation.IsValid,
            string.Join(
                Environment.NewLine,
                sequenceValidation.Issues.Select(issue =>
                    $"{issue.Code}: {issue.Field}: {issue.Message}")));

        var rejected = CreateAcknowledgement(
            handoff,
            consumer,
            IntegrationAcknowledgementStatus.Rejected);
        var rejectedValidation = IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            rejected);
        Assert.True(rejectedValidation.IsValid);
        var resultAfterRejectedAcknowledgement = result with
        {
            AcknowledgementMessageId = rejected.MessageId,
            CreatedAtUtc = rejected.CreatedAtUtc.AddMilliseconds(1)
        };
        var invalidSequence = IntegrationContractValidator.ValidateV2Sequence(
            handoff,
            rejected,
            resultAfterRejectedAcknowledgement);
        Assert.Contains(
            invalidSequence.Issues,
            issue => issue.Code == IntegrationErrorCode.InvalidState
                && issue.Field == "result");
    }

    private static IntegrationAcknowledgementV2 CreateAcknowledgement(
        IntegrationHandoffV2 handoff,
        IntegrationApplicationIdentity producer,
        IntegrationAcknowledgementStatus status) =>
        new(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc.AddMilliseconds(status == IntegrationAcknowledgementStatus.Accepted ? 1 : 2),
            producer,
            status,
            status == IntegrationAcknowledgementStatus.Rejected
                ? new IntegrationError(
                    IntegrationErrorCode.RequestRejected,
                    "The adapter does not support this request shape.",
                    false)
                : null);

    private sealed record CustomerNeutralInspectionRequest(
        Guid TransactionId,
        string ProjectId,
        string SequenceId,
        string StepId,
        string CameraId,
        string AcquisitionId,
        string FrameId,
        IntegrationInspectionModality Modality,
        IntegrationInspectionInputKind InputKind,
        IntegrationApplicationIdentity ConsumerBuild,
        IntegrationArtifactReference InputArtifact,
        IntegrationArtifactReference RecipeArtifact,
        bool RequiresExplicitExecutionApproval);

    private sealed record AdapterShape(
        IntegrationInspectionInputKind InputKind,
        string ConsumerApplicationId);

    private static class CustomerNeutralAdapterContract
    {
        public static CustomerNeutralInspectionRequest Interpret(
            IntegrationHandoffV2 handoff)
        {
            var validation = IntegrationContractValidator.Validate(handoff);
            if (!validation.IsValid)
            {
                var issue = validation.Issues[0];
                throw new IntegrationContractException(issue.Code, issue.Message);
            }

            var context = handoff.Context;
            var expected = context.Modality switch
            {
                IntegrationInspectionModality.TwoD => new AdapterShape(
                    IntegrationInspectionInputKind.Image,
                    IntegrationApplicationIds.TwoDStudio),
                IntegrationInspectionModality.ThreeD => new AdapterShape(
                    IntegrationInspectionInputKind.HeightMap,
                    IntegrationApplicationIds.ThreeDStudio),
                _ => throw new IntegrationContractException(
                    IntegrationErrorCode.RequestRejected,
                    "The customer-neutral adapter contract supports only TwoD/Image and ThreeD/HeightMap.")
            };
            if (context.InputKind != expected.InputKind
                || !string.Equals(
                    context.ConsumerBuild.ApplicationId,
                    expected.ConsumerApplicationId,
                    StringComparison.Ordinal))
            {
                throw new IntegrationContractException(
                    IntegrationErrorCode.RequestRejected,
                    "The modality, input kind, and consumer application identity do not form a supported mapping.");
            }

            var inputArtifact = context.Artifacts.SingleOrDefault(artifact =>
                artifact.Role == IntegrationArtifactRoles.InspectionSource);
            var recipeArtifact = context.Artifacts.SingleOrDefault(artifact =>
                artifact.Role == IntegrationArtifactRoles.InspectionRecipe);
            if (inputArtifact is null
                || recipeArtifact is null
                || !string.Equals(
                    inputArtifact.Sha256,
                    context.InputSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    recipeArtifact.Sha256,
                    context.RecipeSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IntegrationContractException(
                    IntegrationErrorCode.InvalidArtifact,
                    "The inspection source and recipe artifacts must match the context hashes.");
            }

            return new(
                handoff.TransactionId,
                context.ProjectId,
                context.SequenceId,
                context.StepId,
                context.CameraId,
                context.AcquisitionId,
                context.FrameId,
                context.Modality,
                context.InputKind,
                context.ConsumerBuild,
                inputArtifact,
                recipeArtifact,
                RequiresExplicitExecutionApproval: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                TestStorage.RootPath,
                "mch-032-customer-neutral-adapter-contract",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            ExchangeRoot = Path.Combine(Root, "exchange");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(ExchangeRoot);
            MachineProjectPath = WriteText(
                "machine.ovmachine",
                "{\"schema\":\"machine-project/1.0\"}");
            TwoDRecipePath = WriteText(
                "inspection-2d.json",
                "{\"schema\":\"vision-pipeline/1.0\"}");
            ThreeDRecipePath = WriteText(
                "inspection-3d.json",
                "{\"schema\":\"height-pipeline/1.0\"}");
            ImagePath = WriteBytes("inspection-source.png", [0x89, 0x50, 0x4E, 0x47, 0x01]);
            HeightMapPath = WriteBytes("inspection-source.c3d", [0x43, 0x33, 0x44, 0x01, 0x02]);
        }

        public string ExchangeRoot { get; }

        private string Root { get; }

        private string SourceRoot { get; }

        private string MachineProjectPath { get; }

        private string TwoDRecipePath { get; }

        private string ThreeDRecipePath { get; }

        private string ImagePath { get; }

        private string HeightMapPath { get; }

        public IntegrationApplicationIdentity CreateConsumer(string applicationId) =>
            new(
                applicationId,
                "2.1.0",
                new string('2', 40),
                IntegrationSourceState.Clean);

        public MachineInspectionHandoffRequest CreateRequest(
            IntegrationInspectionModality modality,
            IntegrationInspectionInputKind inputKind,
            IntegrationApplicationIdentity consumer)
        {
            var isThreeD = modality == IntegrationInspectionModality.ThreeD;
            return new(
                "machine-project",
                "machine-project/1.0",
                "sequence-032",
                "inspect-customer-neutral",
                "camera.virtual",
                "camera.virtual/frame/00000032",
                "frame-032",
                "mm",
                MachineProjectPath,
                isThreeD ? HeightMapPath : ImagePath,
                isThreeD ? ThreeDRecipePath : TwoDRecipePath,
                modality,
                inputKind,
                new IntegrationApplicationIdentity(
                    IntegrationApplicationIds.MachineStudio,
                    "0.2.0-dev.59",
                    new string('1', 40),
                    IntegrationSourceState.Clean),
                consumer);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string WriteText(string name, string content)
        {
            var path = Path.Combine(SourceRoot, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private string WriteBytes(string name, byte[] content)
        {
            var path = Path.Combine(SourceRoot, name);
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}
