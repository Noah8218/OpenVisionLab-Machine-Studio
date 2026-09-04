using System.Security.Cryptography;
using System.Text;
using System.Net;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(LayoutStartupTestCollection.Name)]
public sealed class MachineIntegrationViewModelTests : IDisposable
{
    private readonly OpenVisionLanguage _originalLanguage;

    public MachineIntegrationViewModelTests()
    {
        _originalLanguage = OpenVisionLanguageService.CurrentLanguage;
        OpenVisionLanguageService.SetLanguage(OpenVisionLanguage.English, save: false);
    }

    public void Dispose() =>
        OpenVisionLanguageService.SetLanguage(_originalLanguage, save: false);

    [Fact]
    public async Task SetupRoundTripAndExplicitRefreshDoNotRunInspection()
    {
        using var fixture = new IntegrationFixture();
        var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false);

        Assert.Contains("No folder was scanned", viewModel.StatusText);

        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionRecipePath = fixture.RecipePath;
        viewModel.TwoDConsumerVersion = "2.1.0";
        viewModel.TwoDConsumerCommit = new string('2', 40);
        viewModel.TcpListenAddress = IPAddress.Loopback.ToString();
        viewModel.TcpListenPortText = "45111";
        viewModel.TcpPeerHost = IPAddress.Loopback.ToString();
        viewModel.TcpPeerPortText = "45112";
        var encodedKey = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes("setup-key")));
        viewModel.SetSessionSharedKey(encodedKey);
        viewModel.SaveSetupCommand.Execute(null);

        Assert.DoesNotContain(
            encodedKey,
            File.ReadAllText(fixture.SettingsPath),
            StringComparison.Ordinal);

        var reloaded = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false);

        Assert.Equal(fixture.ExchangeRoot, reloaded.ExchangeRoot);
        Assert.Equal(fixture.RecipePath, reloaded.InspectionRecipePath);
        Assert.Equal("2.1.0", reloaded.TwoDConsumerVersion);
        Assert.Equal(new string('2', 40), reloaded.TwoDConsumerCommit);
        Assert.Equal(IPAddress.Loopback.ToString(), reloaded.TcpListenAddress);
        Assert.Equal("45111", reloaded.TcpListenPortText);
        Assert.Equal(IPAddress.Loopback.ToString(), reloaded.TcpPeerHost);
        Assert.Equal("45112", reloaded.TcpPeerPortText);
        Assert.True(reloaded.CanRefreshResults);

        reloaded.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() => !reloaded.IsBusy && reloaded.StatusText.Contains("0", StringComparison.Ordinal));

        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
        Assert.Contains("No inspection", reloaded.StatusText);

        reloaded.ResetSetupCommand.Execute(null);

        Assert.Equal(string.Empty, reloaded.ExchangeRoot);
        Assert.Equal(string.Empty, reloaded.InspectionRecipePath);
        Assert.Equal(string.Empty, reloaded.TwoDConsumerVersion);
        Assert.Equal(string.Empty, reloaded.TwoDConsumerCommit);
        Assert.Equal("127.0.0.1", reloaded.TcpListenAddress);
        Assert.Equal("45101", reloaded.TcpListenPortText);
        Assert.Equal("127.0.0.1", reloaded.TcpPeerHost);
        Assert.Equal("45102", reloaded.TcpPeerPortText);
    }

    [Fact]
    public void BrowseCommandsUseInjectedSelectorsAndPreserveCancellation()
    {
        using var fixture = new IntegrationFixture();
        var selectedExchangeRoot = Path.Combine(fixture.ExchangeRoot, "selected");
        var selectedRecipePath = Path.Combine(fixture.ExchangeRoot, "selected-recipe.json");
        Directory.CreateDirectory(selectedExchangeRoot);
        File.WriteAllText(selectedRecipePath, "{}", new UTF8Encoding(false));
        var exchangeSelectorCalls = 0;
        var recipeSelectorCalls = 0;
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false,
            currentPath =>
            {
                exchangeSelectorCalls++;
                Assert.Equal(string.Empty, currentPath);
                return selectedExchangeRoot;
            },
            currentPath =>
            {
                recipeSelectorCalls++;
                Assert.Equal(string.Empty, currentPath);
                return selectedRecipePath;
            });

        viewModel.BrowseExchangeRootCommand.Execute(null);
        viewModel.BrowseRecipeCommand.Execute(null);

        Assert.Equal(1, exchangeSelectorCalls);
        Assert.Equal(1, recipeSelectorCalls);
        Assert.Equal(selectedExchangeRoot, viewModel.ExchangeRoot);
        Assert.Equal(selectedRecipePath, viewModel.InspectionRecipePath);

        using var cancelledViewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false,
            _ => null,
            _ => null);
        cancelledViewModel.ExchangeRoot = fixture.ExchangeRoot;
        cancelledViewModel.InspectionRecipePath = fixture.RecipePath;
        cancelledViewModel.BrowseExchangeRootCommand.Execute(null);
        cancelledViewModel.BrowseRecipeCommand.Execute(null);

        Assert.Equal(fixture.ExchangeRoot, cancelledViewModel.ExchangeRoot);
        Assert.Equal(fixture.RecipePath, cancelledViewModel.InspectionRecipePath);
    }

    [Fact]
    public async Task TcpCommandsPushAndPullLatestTransactionWithoutRunningInspection()
    {
        using var fixture = new IntegrationFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.0.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("vm-tcp-key"));
        var encodedKey = Convert.ToBase64String(key);
        await using var receiver = new MachineIntegrationTcpExchange(
            fixture.RemoteExchangeRoot,
            key);
        var endpoint = await receiver.StartListeningAsync(IPAddress.Loopback, 0);
        using var viewModel = fixture.CreateViewModel(
            (recipePath, requestedConsumer) => fixture.CreateRequest(
                recipePath,
                producer,
                requestedConsumer),
            (_, _) => true);

        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionRecipePath = fixture.RecipePath;
        viewModel.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.TcpListenPortText = "45113";
        viewModel.TcpPeerPortText = endpoint.Port.ToString();
        viewModel.SetSessionSharedKey(encodedKey);
        viewModel.SaveSetupCommand.Execute(null);

        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsBusy && viewModel.CanPushLatestTransaction);
        viewModel.PushLatestTransactionCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsTcpBusy
            && receiver.DiscoverTransactions().Count == 1);

        var received = Assert.Single(receiver.DiscoverTransactions());
        Assert.False(received.HasAcknowledgement);
        Assert.False(received.HasResult);
        Assert.Contains("push", viewModel.LastTcpTransferText, StringComparison.OrdinalIgnoreCase);

        viewModel.PullLatestTransactionCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsTcpBusy
            && viewModel.LastTcpTransferText.Contains("pull", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("No validated Result", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.False(received.HasAcknowledgement);
        Assert.False(received.HasResult);
    }

    [Fact]
    public async Task PublishAndRefreshUseExplicitCommandsAndProjectResult()
    {
        using var fixture = new IntegrationFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.0.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        var viewModel = fixture.CreateViewModel(
            (recipePath, requestedConsumer) => fixture.CreateRequest(
                recipePath,
                producer,
                requestedConsumer),
            (_, _) => true);

        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionRecipePath = fixture.RecipePath;
        viewModel.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.TwoDConsumerCommit = consumer.SourceCommit;

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);

        var handoff = MachineIntegrationExchange
            .DiscoverTransactions(fixture.ExchangeRoot)
            .Single()
            .Handoff;
        Assert.Equal(IntegrationApplicationIds.TwoDStudio, handoff.Context.ConsumerBuild.ApplicationId);
        Assert.Equal(IntegrationInspectionModality.TwoD, handoff.Context.Modality);
        Assert.Equal(IntegrationInspectionInputKind.Image, handoff.Context.InputKind);
        Assert.Contains("Handoff", viewModel.HandoffStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No validated Result", viewModel.ResultStatusText, StringComparison.Ordinal);

        PublishPassResult(fixture.ExchangeRoot, handoff, consumer);
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.ResultStatusText.Contains("Pass", StringComparison.Ordinal));

        Assert.Contains("Pass", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("Completed", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("run-1", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("Accepted", viewModel.AcknowledgementStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshProjectsRejectedAcknowledgementWithoutPublishingResult()
    {
        using var fixture = new IntegrationFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.0.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        var viewModel = fixture.CreateViewModel(
            (recipePath, requestedConsumer) => fixture.CreateRequest(
                recipePath,
                producer,
                requestedConsumer),
            (_, _) => true);

        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionRecipePath = fixture.RecipePath;
        viewModel.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);

        var handoff = MachineIntegrationExchange
            .DiscoverTransactions(fixture.ExchangeRoot)
            .Single()
            .Handoff;
        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc,
            consumer,
            IntegrationAcknowledgementStatus.Rejected,
            new IntegrationError(
                IntegrationErrorCode.RequestRejected,
                "The consumer recipe rejected this Handoff.",
                false));
        File.WriteAllBytes(
            Path.Combine(
                fixture.ExchangeRoot,
                IntegrationTransactionLayout.TransactionsDirectoryName,
                handoff.TransactionId.ToString("D"),
                IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(acknowledgement));

        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.AcknowledgementStatusText.Contains("Rejected", StringComparison.Ordinal));

        Assert.Contains("Rejected", viewModel.AcknowledgementStatusText, StringComparison.Ordinal);
        Assert.Contains("Rejected", viewModel.HandoffStatusText, StringComparison.Ordinal);
        Assert.Contains("No validated Result", viewModel.ResultStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultFileChangeRefreshesProjectionStatusWithoutExplicitCommand()
    {
        using var fixture = new IntegrationFixture();
        var producer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.MachineStudio,
            "1.0.0",
            new string('1', 40),
            IntegrationSourceState.Clean);
        var consumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        using var viewModel = fixture.CreateViewModel(
            (recipePath, requestedConsumer) => fixture.CreateRequest(
                recipePath,
                producer,
                requestedConsumer),
            (_, _) => true);

        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionRecipePath = fixture.RecipePath;
        viewModel.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);

        var handoff = MachineIntegrationExchange
            .DiscoverTransactions(fixture.ExchangeRoot)
            .Single()
            .Handoff;
        PublishPassResult(fixture.ExchangeRoot, handoff, consumer, includeProjection: true);

        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.ProjectionStatusText.Contains("2D", StringComparison.Ordinal)
            && viewModel.ProjectionStatusText.Contains("3D", StringComparison.Ordinal));

        Assert.Contains("2D", viewModel.ProjectionStatusText, StringComparison.Ordinal);
        Assert.Contains("3D", viewModel.ProjectionStatusText, StringComparison.Ordinal);
    }

    private static void PublishPassResult(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IntegrationApplicationIdentity consumer,
        bool includeProjection = false)
    {
        var transactionDirectory = Path.Combine(
            exchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"));
        var runRecordPath = Path.Combine(
            transactionDirectory,
            IntegrationTransactionLayout.ArtifactsDirectoryName,
            "run-record.json");
        var runRecordBytes = Encoding.UTF8.GetBytes("{\"runId\":\"run-1\"}");
        File.WriteAllBytes(runRecordPath, runRecordBytes);

        var evidence = new List<IntegrationArtifactReference>();
        if (includeProjection)
        {
            var projectionPath = Path.Combine(
                transactionDirectory,
                IntegrationTransactionLayout.ArtifactsDirectoryName,
                "coordinate-projection-result.json");
            var projection = new MachineCoordinateProjectionResult(
                MachineCoordinateProjectionContract.SchemaVersion,
                "projection-test",
                Guid.NewGuid().ToString("D"),
                handoff.TransactionId.ToString("D"),
                "Pass",
                "2d-run-1",
                "run-1",
                640,
                480,
                1280,
                840,
                [new MachineProjectedCoordinate(
                    "2D->3D",
                    "2d-0",
                    "rectangle",
                    "test",
                    10,
                    20,
                    20,
                    40,
                    100,
                    "Valid",
                    "OK")],
                [new MachineProjectedCoordinate(
                    "3D->2D",
                    "3d-0",
                    "roi",
                    "test",
                    10,
                    20,
                    20,
                    40,
                    100,
                    "Valid",
                    "Pass")],
                DateTimeOffset.UtcNow);
            var projectionBytes = Encoding.UTF8.GetBytes(
                MachineCoordinateProjectionContract.SerializeResult(projection));
            File.WriteAllBytes(projectionPath, projectionBytes);
            evidence.Add(new IntegrationArtifactReference(
                MachineCoordinateProjectionContract.ResultEvidenceRole,
                MachineCoordinateProjectionContract.ResultEvidenceArtifactId,
                "artifacts/coordinate-projection-result.json",
                projectionBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(projectionBytes))));
        }

        var acknowledgement = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc,
            consumer,
            IntegrationAcknowledgementStatus.Accepted,
            null);
        var result = new IntegrationResultV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Result,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            acknowledgement.MessageId,
            acknowledgement.CreatedAtUtc,
            consumer,
            IntegrationResultStatus.Completed,
            IntegrationInspectionOutcome.Pass,
            "run-1",
            new IntegrationArtifactReference(
                IntegrationArtifactRoles.RunRecord,
                "run-1",
                "artifacts/run-record.json",
                runRecordBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(runRecordBytes))),
            IntegrationRunCorrelation.FromContext(handoff.Context),
            [],
            evidence,
            null);

        File.WriteAllBytes(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(acknowledgement));
        File.WriteAllBytes(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            IntegrationContractJson.SerializeCanonical(result));
    }

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

        Assert.True(condition(), "The integration command did not reach the expected state.");
    }

    private sealed class IntegrationFixture : IDisposable
    {
        public IntegrationFixture()
        {
            Root = Path.Combine(
                "D:\\OpenVisionLab-TestData\\OpenVisionLab-Machine-Studio",
                "machine-integration-viewmodel-tests",
                Guid.NewGuid().ToString("N"));
            ExchangeRoot = Path.Combine(Root, "exchange");
            RemoteExchangeRoot = Path.Combine(Root, "remote-exchange");
            SourceRoot = Path.Combine(Root, "source");
            SettingsPath = Path.Combine(Root, "settings", "integration.json");
            Directory.CreateDirectory(ExchangeRoot);
            Directory.CreateDirectory(RemoteExchangeRoot);
            Directory.CreateDirectory(SourceRoot);
            File.WriteAllText(
                ProjectPath,
                "{\"schema\":\"machine-project/1.0\"}",
                new UTF8Encoding(false));
            File.WriteAllBytes(SourcePath, [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(RecipePath, "{\"tool\":\"local\"}", new UTF8Encoding(false));
        }

        private string Root { get; }
        public string ExchangeRoot { get; }
        public string RemoteExchangeRoot { get; }
        private string SourceRoot { get; }
        public string SettingsPath { get; }
        public string ProjectPath => Path.Combine(SourceRoot, "machine.ovmachine");
        public string SourcePath => Path.Combine(SourceRoot, "inspection-source.png");
        public string RecipePath => Path.Combine(SourceRoot, "inspection-recipe.json");

        public MachineIntegrationViewModel CreateViewModel(
            Func<string, IntegrationApplicationIdentity, MachineInspectionHandoffRequest?> requestFactory,
            Func<string, IntegrationApplicationIdentity, bool> canBuildRequest) =>
            new(
                requestFactory,
                canBuildRequest,
                () => "project-1",
                SettingsPath);

        public MachineIntegrationViewModel CreateViewModel(
            Func<string, IntegrationApplicationIdentity, MachineInspectionHandoffRequest?> requestFactory,
            Func<string, IntegrationApplicationIdentity, bool> canBuildRequest,
            Func<string, string?> selectExchangeRoot,
            Func<string, string?> selectRecipe) =>
            new(
                requestFactory,
                canBuildRequest,
                () => "project-1",
                SettingsPath,
                selectExchangeRoot,
                selectRecipe);

        public MachineInspectionHandoffRequest CreateRequest(
            string recipePath,
            IntegrationApplicationIdentity producer,
            IntegrationApplicationIdentity consumer) =>
            new(
                "project-1",
                "machine-project/1.0",
                "sequence-001",
                "inspect-step",
                "camera-virtual",
                "acquisition-1",
                "frame-1",
                "mm",
                ProjectPath,
                SourcePath,
                recipePath,
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
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
