using System.Security.Cryptography;
using System.Text;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Infrastructure.Integration;
using Xunit;

namespace OpenVisionLab.Machine.Infrastructure.Tests;

public sealed class MachineIntegrationHandoffPublisherTests
{
    [Fact]
    public async Task PublishAsync_ProducesConsumerSpecificImageAndHeightMapHandoffs()
    {
        using var fixture = new PublisherFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.4.0",
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

        var imageHandoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.Root,
            fixture.CreateRequest(
                "image",
                "inspection-source.png",
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                producer,
                twoDConsumer));
        var heightMapHandoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.Root,
            fixture.CreateRequest(
                "height",
                "inspection-source.c3d",
                IntegrationInspectionModality.ThreeD,
                IntegrationInspectionInputKind.HeightMap,
                producer,
                threeDConsumer));

        var readImage = MachineIntegrationExchange.ReadHandoff(
            fixture.Root,
            imageHandoff.TransactionId);
        var readHeightMap = MachineIntegrationExchange.ReadHandoff(
            fixture.Root,
            heightMapHandoff.TransactionId);

        Assert.Equal(IntegrationInspectionModality.TwoD, readImage.Context.Modality);
        Assert.Equal(IntegrationInspectionInputKind.Image, readImage.Context.InputKind);
        Assert.Equal(IntegrationApplicationIds.TwoDStudio, readImage.Context.ConsumerBuild.ApplicationId);
        Assert.Equal(IntegrationInspectionModality.ThreeD, readHeightMap.Context.Modality);
        Assert.Equal(IntegrationInspectionInputKind.HeightMap, readHeightMap.Context.InputKind);
        Assert.Equal(IntegrationApplicationIds.ThreeDStudio, readHeightMap.Context.ConsumerBuild.ApplicationId);
    }

    [Fact]
    public async Task PublishAsync_CarriesAndValidatesCoordinateProjectionProfile()
    {
        using var fixture = new PublisherFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.4.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        var profile = MachineCoordinateProjectionContract.CreateDefault(
            "projection-test",
            640,
            480);

        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.Root,
            fixture.CreateRequest(
                    "image-with-projection",
                    "inspection-source.png",
                    IntegrationInspectionModality.TwoD,
                    IntegrationInspectionInputKind.Image,
                    producer,
                    consumer)
                with
                {
                    ProjectionProfile = profile
                });

        var persisted = MachineIntegrationExchange.ReadHandoff(
            fixture.Root,
            handoff.TransactionId);
        var artifact = Assert.Single(persisted.Context.Artifacts.Where(item =>
            item.Role == MachineCoordinateProjectionContract.ProfileArtifactRole));
        var path = Path.Combine(
            fixture.Root,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"),
            artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var restored = MachineCoordinateProjectionContract.ReadProfile(path);

        Assert.Equal(profile, restored);
        Assert.Equal(
            (639.0, 479.0),
            MachineCoordinateProjectionContract.MapImageToGrid(profile, 639, 479, 640, 480));
        Assert.Equal(
            (639.0, 479.0),
            MachineCoordinateProjectionContract.MapGridToImage(profile, 639, 479, 640, 480));
    }

    [Fact]
    public async Task PublishAsync_StagesLocatorTemplateDeclaredByIntegrationRecipe()
    {
        using var fixture = new PublisherFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.4.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);

        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.Root,
            fixture.CreateRequest(
                    "locator",
                    "inspection-source.png",
                    IntegrationInspectionModality.TwoD,
                    IntegrationInspectionInputKind.Image,
                    producer,
                    consumer)
                with
                {
                    InspectionRecipePath = fixture.LocatorRecipePath
                });

        var persisted = MachineIntegrationExchange.ReadHandoff(
            fixture.Root,
            handoff.TransactionId);
        var artifact = Assert.Single(persisted.Context.Artifacts.Where(item =>
            item.Role == "locator-template"));
        Assert.Equal("locator-template", artifact.ArtifactId);
        Assert.Equal("artifacts/locator-template.png", artifact.RelativePath);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.LocatorTemplatePath))),
            artifact.Sha256);
    }

    [Fact]
    public async Task PublishAsync_PreservesInspectionRecipeExtension()
    {
        using var fixture = new PublisherFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.4.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);

        var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.Root,
            fixture.CreateRequest(
                    "xml-recipe",
                    "inspection-source.png",
                    IntegrationInspectionModality.TwoD,
                    IntegrationInspectionInputKind.Image,
                    producer,
                    consumer)
                with
                {
                    InspectionRecipePath = fixture.XmlRecipePath
                });

        var persisted = MachineIntegrationExchange.ReadHandoff(
            fixture.Root,
            handoff.TransactionId);
        var artifact = Assert.Single(persisted.Context.Artifacts.Where(item =>
            item.Role == IntegrationArtifactRoles.InspectionRecipe));

        Assert.Equal("artifacts/inspection-recipe.xml", artifact.RelativePath);
    }

    private sealed class PublisherFixture : IDisposable
    {
        public PublisherFixture()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "integration-publisher-tests",
                Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            Directory.CreateDirectory(SourceRoot);
            File.WriteAllText(
                Path.Combine(SourceRoot, "machine.ovmachine"),
                "{\"schema\":\"machine-project/1.0\"}",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(SourceRoot, "recipe.json"),
                "{\"tool\":\"local\"}",
                new UTF8Encoding(false));
            LocatorTemplatePath = Path.Combine(SourceRoot, "templates", "locator-template.png");
            Directory.CreateDirectory(Path.GetDirectoryName(LocatorTemplatePath)!);
            File.WriteAllBytes(LocatorTemplatePath, [0x89, 0x50, 0x4E, 0x47, 0x00]);
            LocatorRecipePath = Path.Combine(SourceRoot, "locator-recipe.json");
            File.WriteAllText(
                LocatorRecipePath,
                "{\"schemaVersion\":\"locator-relative-blob-integration-recipe-v1\","
                + "\"templateArtifactId\":\"locator-template\","
                + "\"templatePath\":\"templates/locator-template.png\"}",
                new UTF8Encoding(false));
            XmlRecipePath = Path.Combine(SourceRoot, "pipeline.xml");
            File.WriteAllText(
                XmlRecipePath,
                "<VisionPipeline />",
                new UTF8Encoding(false));
            File.WriteAllBytes(
                Path.Combine(SourceRoot, "inspection-source.png"),
                [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllBytes(
                Path.Combine(SourceRoot, "inspection-source.c3d"),
                [0x43, 0x33, 0x44, 0x00]);
        }

        public string Root { get; }

        private string SourceRoot { get; }

        public string LocatorRecipePath { get; }

        public string XmlRecipePath { get; }

        public string LocatorTemplatePath { get; }

        public MachineInspectionHandoffRequest CreateRequest(
            string caseName,
            string sourceFileName,
            IntegrationInspectionModality modality,
            IntegrationInspectionInputKind inputKind,
            IntegrationApplicationIdentity producer,
            IntegrationApplicationIdentity consumer) =>
            new(
                "machine-project",
                "machine-project/1.0",
                "sequence-001",
                $"inspect-{caseName}",
                "camera-virtual",
                $"acquisition-{caseName}",
                $"frame-{caseName}",
                "mm",
                Path.Combine(SourceRoot, "machine.ovmachine"),
                Path.Combine(SourceRoot, sourceFileName),
                Path.Combine(SourceRoot, "recipe.json"),
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
