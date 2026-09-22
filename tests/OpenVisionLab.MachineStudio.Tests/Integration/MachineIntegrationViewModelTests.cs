using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Net;
using OpenVisionLab;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
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
    public void InspectionExecutionModeTextDistinguishesSimulationAndExternalInspection()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            fixture.CreateProducer,
            () => context.ProjectId,
            fixture.SettingsPath);

        Assert.Contains("Simulation decision", viewModel.InspectionExecutionModeText, StringComparison.Ordinal);

        viewModel.Setup.WaitForExternalResult = true;

        Assert.Contains("External inspection", viewModel.InspectionExecutionModeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceOwnerUsesLatestContextForAdmissionAndExplicitPublication()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        using var viewModel = new MachineIntegrationViewModel(() => context, fixture.CreateProducer, () => context.ProjectId, fixture.SettingsPath);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.RefreshSourceContext();

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
        context = context with { IsExactCommit = false };
        Assert.False(viewModel.CanPublishTwoDImageHandoff);
        context = fixture.CreateContext("frame-2", "acquisition-2");
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        var observation = await viewModel.ObserveAsync(TimeSpan.FromSeconds(5));
        Assert.True(observation.Outcome == MachineIntegrationParticipantOutcome.Completed,
            observation.Exception?.ToString() ?? viewModel.StatusText);

        var transaction = Assert.Single(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
        var handoffJson = Encoding.UTF8.GetString(IntegrationContractJson.SerializeCanonical(transaction.Handoff));
        Assert.Contains("frame-2", handoffJson, StringComparison.Ordinal);
        Assert.Contains("acquisition-2", handoffJson, StringComparison.Ordinal);
        Assert.Equal(IntegrationApplicationIds.TwoDStudio, transaction.Handoff.Context.ConsumerBuild.ApplicationId);
        Assert.False(transaction.HasAcknowledgement);
        Assert.False(transaction.HasResult);
    }

    [Fact]
    public async Task PublishedTwoDSourceArtifactIsExposedToTheMmiViewer()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            fixture.CreateProducer,
            () => context.ProjectId,
            fixture.SettingsPath);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.RefreshSourceContext();

        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        var observation = await viewModel.ObserveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, observation.Outcome);
        Assert.True(viewModel.HasLatestTwoDImage);
        var imageUri = Assert.IsType<Uri>(viewModel.LatestTwoDImageUri);
        Assert.True(imageUri.IsFile);
        Assert.True(File.Exists(imageUri.LocalPath));
        Assert.Contains("artifacts/inspection-source.png", viewModel.LatestTwoDImageSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultImageEvidenceOverridesTheHandoffSourceForTheMmiViewer()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            fixture.CreateProducer,
            () => context.ProjectId,
            fixture.SettingsPath);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.RefreshSourceContext();

        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            (await viewModel.ObserveAsync(TimeSpan.FromSeconds(5))).Outcome);
        var handoff = Assert.Single(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot)).Handoff;
        PublishPassResult(fixture.ExchangeRoot, handoff, viewModel.Setup.TwoDConsumerIdentity!, includeImageEvidence: true);

        viewModel.RefreshResultsCommand.Execute(null);
        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            (await viewModel.ObserveAsync(TimeSpan.FromSeconds(5))).Outcome);

        var imageUri = Assert.IsType<Uri>(viewModel.LatestTwoDImageUri);
        Assert.Equal("result-preview.png", Path.GetFileName(imageUri.LocalPath));
        Assert.Contains("artifacts/result-preview.png", viewModel.LatestTwoDImageSourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultToSimulationRequiresExplicitApplyAndDispatchesOnce()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        context = context with
        {
            CurrentCamera = context.CurrentCamera! with
            {
                State = VirtualCameraState.AwaitingExternalResult,
                Result = null
            }
        };
        var snapshot = CreateSimulationSnapshot(context.CurrentCamera!);
        var dispatched = 0;
        ApplyExternalInspectionResultCommand? dispatchedCommand = null;
        var dispatchCompletion = new TaskCompletionSource<SimulationCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            fixture.CreateProducer,
            () => context.ProjectId,
            fixture.SettingsPath,
            invokeOnUiThreadAsync: null,
            handleException: null,
            simulationSnapshotProvider: () => snapshot,
            dispatchSimulationCommandAsync: command =>
            {
                Interlocked.Increment(ref dispatched);
                dispatchedCommand = Assert.IsType<ApplyExternalInspectionResultCommand>(command);
                return dispatchCompletion.Task;
            });
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.Setup.WaitForExternalResult = true;
        viewModel.RefreshSourceContext();

        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            (await viewModel.ObserveAsync(TimeSpan.FromSeconds(5))).Outcome);
        var handoff = Assert.Single(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot)).Handoff;
        PublishPassResult(fixture.ExchangeRoot, handoff, viewModel.Setup.TwoDConsumerIdentity!);

        viewModel.RefreshResultsCommand.Execute(null);
        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            (await viewModel.ObserveAsync(TimeSpan.FromSeconds(5))).Outcome);
        await WaitForAsync(() => viewModel.CanApplyResultToSimulation);

        Assert.Equal(0, Volatile.Read(ref dispatched));
        viewModel.ApplyResultToSimulationCommand.Execute(null);
        viewModel.ApplyResultToSimulationCommand.Execute(null);
        Assert.Equal(1, Volatile.Read(ref dispatched));
        Assert.NotNull(dispatchedCommand);
        dispatchCompletion.SetResult(new SimulationCommandResult(
            dispatchedCommand.CommandId,
            true,
            snapshot.TickIndex,
            snapshot.SimulationTime,
            SimulationCommandErrorCode.None,
            "accepted"));
        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            (await viewModel.ObserveAsync(TimeSpan.FromSeconds(5))).Outcome);

        Assert.Equal(1, Volatile.Read(ref dispatched));
        Assert.Equal(handoff.TransactionId, dispatchedCommand.MessageChain.HandoffTransactionId);
        Assert.False(viewModel.CanApplyResultToSimulation);
    }

    [Fact]
    public async Task AutomaticExternalPublishAndRefreshAppliesAcceptedResultWithoutManualCommand()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        context = context with
        {
            CurrentCamera = context.CurrentCamera! with
            {
                State = VirtualCameraState.AwaitingExternalResult,
                Result = null
            }
        };
        var snapshot = CreateSimulationSnapshot(context.CurrentCamera!);
        var dispatched = 0;
        ApplyExternalInspectionResultCommand? dispatchedCommand = null;
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            fixture.CreateProducer,
            () => context.ProjectId,
            fixture.SettingsPath,
            simulationSnapshotProvider: () => snapshot,
            dispatchSimulationCommandAsync: command =>
            {
                Interlocked.Increment(ref dispatched);
                dispatchedCommand = Assert.IsType<ApplyExternalInspectionResultCommand>(command);
                return Task.FromResult(new SimulationCommandResult(
                    command.CommandId,
                    true,
                    snapshot.TickIndex,
                    snapshot.SimulationTime,
                    SimulationCommandErrorCode.None,
                    "accepted"));
            });
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.Setup.WaitForExternalResult = true;
        viewModel.RefreshSourceContext();

        var published = await viewModel.PublishAutomaticExternalInspectionAsync();
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, published.Outcome);
        var handoff = Assert.Single(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot)).Handoff;
        PublishPassResult(fixture.ExchangeRoot, handoff, viewModel.Setup.TwoDConsumerIdentity!);

        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsBusy && Volatile.Read(ref dispatched) == 1);

        Assert.Equal(1, Volatile.Read(ref dispatched));
        Assert.NotNull(dispatchedCommand);
        Assert.Equal(handoff.TransactionId, dispatchedCommand!.MessageChain.HandoffTransactionId);
        Assert.False(viewModel.CanApplyResultToSimulation);
    }

    [Fact]
    public async Task AutomaticExternalRejectedAcknowledgementInvokesAbortCallback()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext() with
        {
            CurrentCamera = fixture.CreateContext().CurrentCamera! with
            {
                State = VirtualCameraState.AwaitingExternalResult,
                Result = null
            }
        };
        var snapshot = CreateSimulationSnapshot(context.CurrentCamera!);
        var aborts = 0;
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            fixture.CreateProducer,
            () => context.ProjectId,
            fixture.SettingsPath,
            simulationSnapshotProvider: () => snapshot,
            dispatchSimulationCommandAsync: command => Task.FromResult(new SimulationCommandResult(
                command.CommandId,
                true,
                snapshot.TickIndex,
                snapshot.SimulationTime,
                SimulationCommandErrorCode.None,
                "accepted")),
            abortAutomaticExternalInspectionAsync: () =>
            {
                Interlocked.Increment(ref aborts);
                return Task.CompletedTask;
            });
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.Setup.WaitForExternalResult = true;
        viewModel.RefreshSourceContext();

        var published = await viewModel.PublishAutomaticExternalInspectionAsync();
        Assert.Equal(MachineIntegrationParticipantOutcome.Completed, published.Outcome);
        var handoff = Assert.Single(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot)).Handoff;
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"));
        var rejected = new IntegrationAcknowledgementV2(
            IntegrationContractSchema.V2,
            IntegrationMessageKind.Acknowledgement,
            Guid.NewGuid(),
            handoff.TransactionId,
            handoff.MessageId,
            handoff.CreatedAtUtc,
            viewModel.Setup.TwoDConsumerIdentity!,
            IntegrationAcknowledgementStatus.Rejected,
            new IntegrationError(
                IntegrationErrorCode.RequestRejected,
                "The consumer rejected this automatic Handoff.",
                false));
        File.WriteAllBytes(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(rejected));

        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsBusy && Volatile.Read(ref aborts) == 1);

        Assert.Equal(1, Volatile.Read(ref aborts));
        Assert.Contains("Rejected", viewModel.AcknowledgementStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.CanApplyResultToSimulation);
    }

    [Fact]
    public async Task PublishFailureKeepsStatusAndForwardsStructuredExceptionContext()
    {
        using var fixture = new IntegrationFixture();
        var expected = new InvalidOperationException("handoff preparation failed");
        Exception? captured = null;
        using var viewModel = new MachineIntegrationViewModel(
            (_, _) => throw expected,
            (_, _) => true,
            () => "project-1",
            fixture.SettingsPath,
            _ => fixture.ExchangeRoot,
            _ => fixture.RecipePath,
            invokeOnUiThreadAsync: null,
            handleException: exception => captured = exception);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);

        await WaitForAsync(() => captured is not null);

        Assert.Same(expected, captured);
        Assert.Equal(expected.Message, viewModel.StatusText);
    }

    [Fact]
    public void SourceRefreshOwnsDeduplicationBeforeNotificationsAndKeepsExplicitRefresh()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.CreateContext();
        using var viewModel = new MachineIntegrationViewModel(
            () => context,
            () => throw new InvalidOperationException("Source refresh must not read build identity files."),
            () => context.ProjectId, fixture.SettingsPath);
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.CanPublishTwoDImageHandoff))
            {
                notifications++;
                if (notifications == 1)
                {
                    viewModel.RefreshSourceContext();
                }
            }
        };

        viewModel.RefreshSourceContext();
        Assert.Equal(1, notifications);
        context = context with { ProjectSchema = "metadata-only" };
        viewModel.RefreshSourceContext();
        Assert.Equal(1, notifications);
        context = context with { ProjectPath = fixture.ProjectPath + ".copy" };
        viewModel.RefreshSourceContext();
        Assert.Equal(2, notifications);
        viewModel.RefreshSourceContext();
        Assert.Equal(2, notifications);
        viewModel.RefreshContext();
        Assert.Equal(3, notifications);
        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
    }

    [Fact]
    public void SourceOwnerCloseAndDisposeBlockRequestsWithoutReadingContext()
    {
        using var fixture = new IntegrationFixture();
        var reads = 0;
        using var viewModel = new MachineIntegrationViewModel(
            () => { reads++; return fixture.CreateContext(); },
            fixture.CreateProducer,
            () => "project-1", fixture.SettingsPath);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        Assert.True(viewModel.CanPublishTwoDImageHandoff);

        viewModel.SetSessionCloseAdmission(true);
        var beforeBlockedReads = reads;
        Assert.False(viewModel.CanPublishTwoDImageHandoff);
        Assert.Equal(beforeBlockedReads, reads);
        viewModel.SetSessionCloseAdmission(false);
        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        viewModel.Dispose();
        beforeBlockedReads = reads;
        Assert.False(viewModel.PublishTwoDImageHandoffCommand.CanExecute(null));
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        Assert.Equal(beforeBlockedReads, reads);
        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
    }

    [Fact]
    public async Task SetupRoundTripAndExplicitRefreshDoNotRunInspection()
    {
        using var fixture = new IntegrationFixture();
        var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false);

        Assert.Contains("No folder was scanned", viewModel.StatusText);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.Setup.TcpListenAddress = IPAddress.Loopback.ToString();
        viewModel.Setup.TcpListenPortText = "45111";
        viewModel.Setup.TcpPeerHost = IPAddress.Loopback.ToString();
        viewModel.Setup.TcpPeerPortText = "45112";
        var encodedKey = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes("setup-key")));
        viewModel.SetSessionSharedKey(encodedKey);
        viewModel.Setup.SaveSetupCommand.Execute(null);

        Assert.DoesNotContain(
            encodedKey,
            File.ReadAllText(fixture.SettingsPath),
            StringComparison.Ordinal);

        var reloaded = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false);

        Assert.Equal(fixture.ExchangeRoot, reloaded.Setup.ExchangeRoot);
        Assert.Equal(fixture.RecipePath, reloaded.Setup.InspectionRecipePath);
        Assert.Equal("2.1.0", reloaded.Setup.TwoDConsumerVersion);
        Assert.Equal(new string('2', 40), reloaded.Setup.TwoDConsumerCommit);
        Assert.Equal(IPAddress.Loopback.ToString(), reloaded.Setup.TcpListenAddress);
        Assert.Equal("45111", reloaded.Setup.TcpListenPortText);
        Assert.Equal(IPAddress.Loopback.ToString(), reloaded.Setup.TcpPeerHost);
        Assert.Equal("45112", reloaded.Setup.TcpPeerPortText);
        Assert.True(reloaded.CanRefreshResults);

        reloaded.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() => !reloaded.IsBusy && reloaded.StatusText.Contains("0", StringComparison.Ordinal));

        Assert.Empty(MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot));
        Assert.Contains("No inspection", reloaded.StatusText);

        var resetVersion = reloaded.Setup.ResetVersion;
        reloaded.Setup.ResetSetupCommand.Execute(null);

        Assert.Equal(resetVersion + 1, reloaded.Setup.ResetVersion);
        Assert.Equal(string.Empty, reloaded.Setup.ExchangeRoot);
        Assert.Equal(string.Empty, reloaded.Setup.InspectionRecipePath);
        Assert.Equal(string.Empty, reloaded.Setup.TwoDConsumerVersion);
        Assert.Equal(string.Empty, reloaded.Setup.TwoDConsumerCommit);
        Assert.Equal("127.0.0.1", reloaded.Setup.TcpListenAddress);
        Assert.Equal("45101", reloaded.Setup.TcpListenPortText);
        Assert.Equal("127.0.0.1", reloaded.Setup.TcpPeerHost);
        Assert.Equal("45102", reloaded.Setup.TcpPeerPortText);
    }

    [Fact]
    public void MmiRecipeCatalogRestoresActiveSelectionAndRemovalKeepsSourceFile()
    {
        using var fixture = new IntegrationFixture();
        var alternateRecipePath = Path.Combine(
            Path.GetDirectoryName(fixture.RecipePath)!,
            "alternate-recipe.json");
        File.WriteAllText(alternateRecipePath, "{\"tool\":\"alternate\"}", new UTF8Encoding(false));

        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false,
            _ => null,
            _ => alternateRecipePath);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.AddRecipeCommand.Execute(null);
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.SaveSetupCommand.Execute(null);

        Assert.Equal(
            new[] { alternateRecipePath, fixture.RecipePath },
            viewModel.Setup.RecipeCatalogItems.Select(item => item.Path));
        Assert.Equal(fixture.RecipePath, viewModel.Setup.SelectedRecipe?.Path);

        using var reloaded = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false,
            _ => null,
            _ => null);
        Assert.Equal(
            new[] { alternateRecipePath, fixture.RecipePath },
            reloaded.Setup.RecipeCatalogItems.Select(item => item.Path));
        Assert.Equal(fixture.RecipePath, reloaded.Setup.SelectedRecipe?.Path);

        reloaded.Setup.RemoveRecipeCommand.Execute(reloaded.Setup.SelectedRecipe);

        Assert.Equal(alternateRecipePath, reloaded.Setup.SelectedRecipe?.Path);
        Assert.True(File.Exists(fixture.RecipePath));
        Assert.True(File.Exists(alternateRecipePath));
    }

    [Fact]
    public void MmiSetupExposesUnsavedStateUntilSetupIsSaved()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false,
            _ => null,
            _ => null);

        Assert.False(viewModel.Setup.HasUnsavedChanges);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;

        Assert.True(viewModel.Setup.HasUnsavedChanges);
        Assert.Contains("Unsaved", viewModel.Setup.SetupSaveStateText, StringComparison.Ordinal);

        viewModel.Setup.SaveSetupCommand.Execute(null);

        Assert.False(viewModel.Setup.HasUnsavedChanges);
        Assert.Contains("saved", viewModel.Setup.SetupSaveStateText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupWorkflowStatusGuidesTheNextMissingStep()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false);

        Assert.Contains("exchange", viewModel.SetupWorkflowStatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        Assert.Contains("recipe", viewModel.SetupWorkflowStatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        Assert.Contains("shared key", viewModel.SetupWorkflowStatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.SetSessionSharedKey(Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes("setup-workflow-key"))));
        Assert.Contains("Save", viewModel.SetupWorkflowStatusText, StringComparison.OrdinalIgnoreCase);

        viewModel.Setup.SaveSetupCommand.Execute(null);
        Assert.Contains("Setup ready", viewModel.SetupWorkflowStatusText, StringComparison.OrdinalIgnoreCase);
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

        viewModel.Setup.BrowseExchangeRootCommand.Execute(null);
        viewModel.Setup.BrowseRecipeCommand.Execute(null);

        Assert.Equal(1, exchangeSelectorCalls);
        Assert.Equal(1, recipeSelectorCalls);
        Assert.Equal(selectedExchangeRoot, viewModel.Setup.ExchangeRoot);
        Assert.Equal(selectedRecipePath, viewModel.Setup.InspectionRecipePath);

        using var cancelledViewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => false,
            _ => null,
            _ => null);
        cancelledViewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        cancelledViewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        cancelledViewModel.Setup.BrowseExchangeRootCommand.Execute(null);
        cancelledViewModel.Setup.BrowseRecipeCommand.Execute(null);

        Assert.Equal(fixture.ExchangeRoot, cancelledViewModel.Setup.ExchangeRoot);
        Assert.Equal(fixture.RecipePath, cancelledViewModel.Setup.InspectionRecipePath);
    }

    [Fact]
    public void DisposedIntegrationCommandsCannotExecute()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        Assert.True(viewModel.CanRefreshResults);

        viewModel.Dispose();

        Assert.False(viewModel.CanPublishTwoDImageHandoff);
        Assert.False(viewModel.CanRefreshResults);
        Assert.False(viewModel.PublishTwoDImageHandoffCommand.CanExecute(null));
        Assert.False(viewModel.RefreshResultsCommand.CanExecute(null));
    }

    [Fact]
    public void DisposeNotifiesParentIntegrationCommandsOfFinalAdmission()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        Assert.True(viewModel.CanRefreshResults);
        var notifications = new int[2];
        viewModel.PublishTwoDImageHandoffCommand.CanExecuteChanged += (_, _) => notifications[0]++;
        viewModel.RefreshResultsCommand.CanExecuteChanged += (_, _) => notifications[1]++;

        viewModel.Dispose();

        Assert.False(viewModel.PublishTwoDImageHandoffCommand.CanExecute(null));
        Assert.False(viewModel.RefreshResultsCommand.CanExecute(null));
        Assert.Equal(1, notifications[0]);
        Assert.Equal(1, notifications[1]);
    }

    [Fact]
    public void DisposeNotifiesSetupCommandsOfFinalAdmission()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);

        Assert.True(viewModel.Setup.SaveSetupCommand.CanExecute(null));
        Assert.True(viewModel.Setup.ResetSetupCommand.CanExecute(null));
        Assert.True(viewModel.Setup.BrowseExchangeRootCommand.CanExecute(null));
        Assert.True(viewModel.Setup.BrowseRecipeCommand.CanExecute(null));
        var notifications = new int[4];
        viewModel.Setup.SaveSetupCommand.CanExecuteChanged += (_, _) => notifications[0]++;
        viewModel.Setup.ResetSetupCommand.CanExecuteChanged += (_, _) => notifications[1]++;
        viewModel.Setup.BrowseExchangeRootCommand.CanExecuteChanged += (_, _) => notifications[2]++;
        viewModel.Setup.BrowseRecipeCommand.CanExecuteChanged += (_, _) => notifications[3]++;

        viewModel.Dispose();

        Assert.False(viewModel.Setup.SaveSetupCommand.CanExecute(null));
        Assert.False(viewModel.Setup.ResetSetupCommand.CanExecute(null));
        Assert.False(viewModel.Setup.BrowseExchangeRootCommand.CanExecute(null));
        Assert.False(viewModel.Setup.BrowseRecipeCommand.CanExecute(null));
        Assert.All(notifications, count => Assert.Equal(1, count));
    }

    [Fact]
    public void DisposeRejectsDirectLocalizationRefresh()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        viewModel.Dispose();
        var notificationCountAfterDispose = notificationCount;

        var exception = Record.Exception(viewModel.RefreshLocalization);

        Assert.Null(exception);
        Assert.Equal(notificationCountAfterDispose, notificationCount);
    }

    [Fact]
    public void DisposeRejectsDirectSessionSharedKeyUpdate()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);
        var encodedKey = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes("dispose-shared-key")));
        viewModel.SetSessionSharedKey(encodedKey);
        Assert.True(viewModel.SetSessionSharedKeyCommand.CanExecute(encodedKey));
        Action<string?> retainedCallback = viewModel.SetSessionSharedKey;
        var sharedKeyStatusBeforeDispose = viewModel.SharedKeyStatusText;
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        viewModel.Dispose();
        var notificationCountAfterDispose = notificationCount;

        var exception = Record.Exception(() => retainedCallback(null));

        Assert.Null(exception);
        Assert.Equal(notificationCountAfterDispose, notificationCount);
        Assert.Equal(sharedKeyStatusBeforeDispose, viewModel.SharedKeyStatusText);
        Assert.False(viewModel.SetSessionSharedKeyCommand.CanExecute(encodedKey));
    }

    [Fact]
    public void DisposeRejectsRetainedSetupResetCallback()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        viewModel.Dispose();
        var notificationCountAfterDispose = notificationCount;
        var resetHandler = typeof(MachineIntegrationViewModel).GetMethod(
            "OnSetupResetCompleted",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(resetHandler);
        var exception = Record.Exception(() => resetHandler!.Invoke(
            viewModel,
            new object?[] { null, EventArgs.Empty }));

        Assert.Null(exception);
        Assert.Equal(notificationCountAfterDispose, notificationCount);
    }

    [Fact]
    public void DisposeRejectsRetainedSourceContextRefresh()
    {
        using var fixture = new IntegrationFixture();
        var contextReads = 0;
        using var viewModel = new MachineIntegrationViewModel(
            () =>
            {
                contextReads++;
                return fixture.CreateContext();
            },
            fixture.CreateProducer,
            () => "project-1",
            fixture.SettingsPath);
        var notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        viewModel.Dispose();
        var contextReadsAfterDispose = contextReads;
        var notificationCountAfterDispose = notificationCount;

        var exception = Record.Exception(viewModel.RefreshSourceContext);

        Assert.Null(exception);
        Assert.Equal(contextReadsAfterDispose, contextReads);
        Assert.Equal(notificationCountAfterDispose, notificationCount);
    }

    [Fact]
    public void DisposedSetupCommandsDoNotInvokeDialogsOrPersistSettings()
    {
        using var fixture = new IntegrationFixture();
        var exchangeRootSelectorCalls = 0;
        var recipeSelectorCalls = 0;
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true,
            _ =>
            {
                exchangeRootSelectorCalls++;
                return fixture.ExchangeRoot;
            },
            _ =>
            {
                recipeSelectorCalls++;
                return fixture.RecipePath;
            });

        viewModel.Dispose();

        viewModel.Setup.BrowseExchangeRootCommand.Execute(null);
        viewModel.Setup.BrowseRecipeCommand.Execute(null);
        viewModel.Setup.SaveSetupCommand.Execute(null);
        viewModel.Setup.ResetSetupCommand.Execute(null);

        Assert.Equal(0, exchangeRootSelectorCalls);
        Assert.Equal(0, recipeSelectorCalls);
    }

    [Fact]
    public void SessionCloseAdmissionBlocksFileAndTcpCommandsUntilReleased()
    {
        using var fixture = new IntegrationFixture();
        using var viewModel = fixture.CreateViewModel(
            (_, _) => null,
            (_, _) => true);

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        Assert.True(viewModel.CanRefreshResults);
        Assert.True(viewModel.PingTcpPeerCommand.CanExecute(null));

        viewModel.SetSessionCloseAdmission(true);

        Assert.False(viewModel.CanPublishTwoDImageHandoff);
        Assert.False(viewModel.CanRefreshResults);
        Assert.False(viewModel.PingTcpPeerCommand.CanExecute(null));

        viewModel.SetSessionCloseAdmission(false);

        Assert.True(viewModel.CanPublishTwoDImageHandoff);
        Assert.True(viewModel.CanRefreshResults);
        Assert.True(viewModel.PingTcpPeerCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposeSuppressesLatePublishStatus()
    {
        using var fixture = new IntegrationFixture();
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestReturned = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = 0;
        var lateStatusChanges = 0;
        using var viewModel = fixture.CreateViewModel(
            (recipePath, consumer) =>
            {
                requestStarted.SetResult();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseRequest) != 0);
                requestReturned.SetResult();
                return fixture.CreateRequest(
                    recipePath,
                    new IntegrationApplicationIdentity(
                        IntegrationApplicationIds.MachineStudio,
                        "1.0.0",
                        new string('1', 40),
                        IntegrationSourceState.Clean),
                    consumer);
            },
            (_, _) => true);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = "2.1.0";
        viewModel.Setup.TwoDConsumerCommit = new string('2', 40);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (Volatile.Read(ref releaseRequest) == 2
                && args.PropertyName == nameof(viewModel.StatusText))
            {
                Interlocked.Increment(ref lateStatusChanges);
            }
        };

        _ = Task.Run(() => viewModel.PublishTwoDImageHandoffCommand.Execute(null));
        await requestStarted.Task;
        Volatile.Write(ref releaseRequest, 1);
        await requestReturned.Task;
        Volatile.Write(ref releaseRequest, 2);
        viewModel.Dispose();

        await WaitForAsync(() => MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);
        Assert.Equal(0, Volatile.Read(ref lateStatusChanges));
    }

    [Fact]
    public async Task ObserveAsyncWaitsForAnInFlightPublishOperation()
    {
        using var fixture = new IntegrationFixture();
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
            (recipePath, requestedConsumer) =>
            {
                requestStarted.SetResult();
                releaseRequest.Task.GetAwaiter().GetResult();
                return fixture.CreateRequest(recipePath, producer, requestedConsumer);
            },
            (_, _) => true);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
        Assert.True(viewModel.CanPublishTwoDImageHandoff);

        var commandTask = Task.Run(() => viewModel.PublishTwoDImageHandoffCommand.Execute(null));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observationTask = viewModel.ObserveAsync(TimeSpan.FromSeconds(2));

        Assert.False(observationTask.IsCompleted);
        releaseRequest.SetResult();
        var observation = await observationTask.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !viewModel.IsBusy);

        Assert.Equal(
            MachineIntegrationParticipantOutcome.Completed,
            observation.Outcome);
        Assert.Equal(
            MachineIntegrationOperationKind.PublishHandoff,
            observation.FileOperation?.Kind);
        await commandTask;
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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.Setup.TcpListenPortText = "45113";
        viewModel.Setup.TcpPeerPortText = endpoint.Port.ToString();
        viewModel.SetSessionSharedKey(encodedKey);
        viewModel.Setup.SaveSetupCommand.Execute(null);

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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;

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
        var pendingHistoryRow = Assert.Single(viewModel.TransactionHistoryRows);
        Assert.Equal(viewModel.TransactionHistory.Single().Handoff.TransactionId, pendingHistoryRow.Handoff.TransactionId);
        Assert.Equal(
            $"TX-{handoff.TransactionId.ToString("N")[..8].ToUpperInvariant()}",
            pendingHistoryRow.ShortTransactionId);
        Assert.Equal(handoff.TransactionId.ToString("D"), pendingHistoryRow.FullTransactionId);
        Assert.Contains("Pending", pendingHistoryRow.StatusText, StringComparison.Ordinal);

        PublishPassResult(fixture.ExchangeRoot, handoff, consumer);
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.ResultStatusText.Contains("Pass", StringComparison.Ordinal));

        Assert.Contains("Pass", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("Completed", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("run-1", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("Accepted", viewModel.AcknowledgementStatusText, StringComparison.Ordinal);
        Assert.Contains("Result published", Assert.Single(viewModel.TransactionHistoryRows).StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModalityResultStatusKeepsTwoDAndThreeDResultsSeparate()
    {
        using var fixture = new IntegrationFixture();
        var producer = fixture.CreateProducer();
        var twoDConsumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.TwoDStudio,
            "2.1.0",
            new string('2', 40),
            IntegrationSourceState.Clean);
        var threeDConsumer = new IntegrationApplicationIdentity(
            IntegrationApplicationIds.ThreeDStudio,
            "0.2.0",
            new string('3', 40),
            IntegrationSourceState.Clean);
        var twoDHandoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                fixture.RecipePath,
                fixture.SourcePath,
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                producer,
                twoDConsumer));
        var threeDHandoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            fixture.ExchangeRoot,
            fixture.CreateRequest(
                fixture.ThreeDRecipePath,
                fixture.ThreeDSourcePath,
                IntegrationInspectionModality.ThreeD,
                IntegrationInspectionInputKind.HeightMap,
                producer,
                threeDConsumer));
        using var viewModel = fixture.CreateViewModel((_, _) => null, (_, _) => false);
        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;

        // Publish the newer modality first and refresh before the older Result
        // arrives; the projection must retain both modality-specific states.
        PublishPassResult(fixture.ExchangeRoot, threeDHandoff, threeDConsumer, includeImageEvidence: true);
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.LatestThreeDResultStatusText.Contains("3D result", StringComparison.Ordinal));

        PublishPassResult(fixture.ExchangeRoot, twoDHandoff, twoDConsumer, includeImageEvidence: true);
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.LatestTwoDResultStatusText.Contains("2D result", StringComparison.Ordinal)
            && viewModel.LatestThreeDResultStatusText.Contains("3D result", StringComparison.Ordinal));

        Assert.Contains("Pass", viewModel.LatestTwoDResultStatusText, StringComparison.Ordinal);
        Assert.Contains("Pass", viewModel.LatestThreeDResultStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.HasLatestTwoDImage);
        Assert.True(viewModel.HasLatestThreeDImage);
        Assert.Equal(twoDHandoff.TransactionId, viewModel.TransactionHistory
            .Single(transaction => transaction.Handoff.Context.Modality == IntegrationInspectionModality.TwoD)
            .Handoff.TransactionId);
        Assert.Equal(threeDHandoff.TransactionId, viewModel.TransactionHistory
            .Single(transaction => transaction.Handoff.Context.Modality == IntegrationInspectionModality.ThreeD)
            .Handoff.TransactionId);
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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
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
        Assert.Contains("Rejected", Assert.Single(viewModel.TransactionHistoryRows).StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidResultDoesNotAppearAsPublishedInHandoffStatus()
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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);

        var handoff = MachineIntegrationExchange
            .DiscoverTransactions(fixture.ExchangeRoot)
            .Single()
            .Handoff;
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"));
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
        File.WriteAllBytes(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.AcknowledgementFileName),
            IntegrationContractJson.SerializeCanonical(acknowledgement));
        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{ malformed-result }");

        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.ResultStatusText.Contains("Result read failed", StringComparison.Ordinal));

        Assert.Contains("Result read failed", viewModel.ResultStatusText, StringComparison.Ordinal);
        Assert.Contains("Result read failed", viewModel.HandoffStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Result published", viewModel.HandoffStatusText, StringComparison.Ordinal);
        Assert.Contains("Result read failed", Assert.Single(viewModel.TransactionHistoryRows).StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransactionDiagnosticFilterChangesVisibleRowsAndResetsWithSetup()
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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);

        var transactionsRoot = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName);
        Directory.CreateDirectory(Path.Combine(
            transactionsRoot,
            $".{Guid.NewGuid():D}.{Guid.NewGuid():N}.staging"));
        Directory.CreateDirectory(Path.Combine(transactionsRoot, "unexpected"));

        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.TransactionDiagnostics.Count == 3);

        Assert.Equal(5, viewModel.TransactionDiagnosticFilters.Count);
        Assert.Equal(viewModel.TransactionHistory.Count, viewModel.TransactionHistoryRows.Count);
        Assert.Null(viewModel.SelectedTransactionDiagnosticFilter?.State);
        Assert.Equal(viewModel.TransactionDiagnostics.Count, viewModel.VisibleTransactionDiagnostics.Count);

        var publishedFilter = viewModel.TransactionDiagnosticFilters.Single(item =>
            item.State == MachineIntegrationTransactionState.Published);
        viewModel.SelectedTransactionDiagnosticFilter = publishedFilter;

        Assert.Same(publishedFilter, viewModel.SelectedTransactionDiagnosticFilter);
        Assert.NotEmpty(viewModel.VisibleTransactionDiagnostics);
        Assert.All(viewModel.VisibleTransactionDiagnostics, diagnostic =>
            Assert.Equal(MachineIntegrationTransactionState.Published, diagnostic.State));
        Assert.Equal(3, viewModel.TransactionDiagnostics.Count);

        var emptyFilter = viewModel.TransactionDiagnosticFilters.Single(item =>
            item.State == MachineIntegrationTransactionState.Quarantined);
        viewModel.SelectedTransactionDiagnosticFilter = emptyFilter;

        Assert.Empty(viewModel.VisibleTransactionDiagnostics);
        Assert.True(viewModel.HasTransactionDiagnosticsEmptyState);
        Assert.Contains("No transactions match", viewModel.TransactionDiagnosticsEmptyText, StringComparison.Ordinal);

        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() => !viewModel.IsBusy && viewModel.TransactionDiagnostics.Count == 3);
        Assert.Same(emptyFilter, viewModel.SelectedTransactionDiagnosticFilter);
        Assert.Empty(viewModel.VisibleTransactionDiagnostics);

        viewModel.Setup.ResetSetupCommand.Execute(null);
        Assert.Null(viewModel.SelectedTransactionDiagnosticFilter?.State);
        Assert.Empty(viewModel.TransactionDiagnostics);
        Assert.Empty(viewModel.TransactionHistoryRows);
    }

    [Fact]
    public async Task TransactionHistoryFilterProjectsStableStatusAndResetsWithSetup()
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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
        viewModel.PublishTwoDImageHandoffCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && MachineIntegrationExchange.DiscoverTransactions(fixture.ExchangeRoot).Count == 1);

        var handoff = MachineIntegrationExchange
            .DiscoverTransactions(fixture.ExchangeRoot)
            .Single()
            .Handoff;
        var transactionDirectory = Path.Combine(
            fixture.ExchangeRoot,
            IntegrationTransactionLayout.TransactionsDirectoryName,
            handoff.TransactionId.ToString("D"));
        var filterNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(viewModel.SelectedTransactionHistoryFilter)
                or nameof(viewModel.VisibleTransactionHistoryRows))
            {
                filterNotifications++;
            }
        };

        Assert.Equal(6, viewModel.TransactionHistoryFilters.Count);
        Assert.Null(viewModel.SelectedTransactionHistoryFilter?.State);
        Assert.Equal(viewModel.TransactionHistory.Count, viewModel.TransactionHistoryRows.Count);
        Assert.Equal(viewModel.TransactionHistory.Count, viewModel.VisibleTransactionHistoryRows.Count);

        var pendingFilter = viewModel.TransactionHistoryFilters.Single(item =>
            item.State == MachineIntegrationTransactionHistoryState.PendingReview);
        viewModel.SelectedTransactionHistoryFilter = pendingFilter;
        Assert.Same(pendingFilter, viewModel.SelectedTransactionHistoryFilter);
        Assert.Single(viewModel.VisibleTransactionHistoryRows);
        Assert.Contains("Pending", viewModel.VisibleTransactionHistoryRows[0].StatusText, StringComparison.Ordinal);
        Assert.True(filterNotifications > 0);

        void WriteAcknowledgement(IntegrationAcknowledgementStatus status, IntegrationError? error) =>
            File.WriteAllBytes(
                Path.Combine(transactionDirectory, IntegrationTransactionLayout.AcknowledgementFileName),
                IntegrationContractJson.SerializeCanonical(new IntegrationAcknowledgementV2(
                    IntegrationContractSchema.V2,
                    IntegrationMessageKind.Acknowledgement,
                    Guid.NewGuid(),
                    handoff.TransactionId,
                    handoff.MessageId,
                    handoff.CreatedAtUtc,
                    consumer,
                    status,
                    error)));

        WriteAcknowledgement(
            IntegrationAcknowledgementStatus.Rejected,
            new IntegrationError(
                IntegrationErrorCode.RequestRejected,
                "The consumer recipe rejected this Handoff.",
                false));
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.HandoffStatusText.Contains("Rejected", StringComparison.Ordinal));

        Assert.Same(pendingFilter, viewModel.SelectedTransactionHistoryFilter);
        Assert.Empty(viewModel.VisibleTransactionHistoryRows);
        Assert.True(viewModel.HasTransactionHistoryEmptyState);

        var rejectedFilter = viewModel.TransactionHistoryFilters.Single(item =>
            item.State == MachineIntegrationTransactionHistoryState.Rejected);
        viewModel.SelectedTransactionHistoryFilter = rejectedFilter;
        Assert.Single(viewModel.VisibleTransactionHistoryRows);
        Assert.Contains("Rejected", viewModel.VisibleTransactionHistoryRows[0].StatusText, StringComparison.Ordinal);

        WriteAcknowledgement(IntegrationAcknowledgementStatus.Accepted, null);
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.HandoffStatusText.Contains("Reviewed", StringComparison.Ordinal));

        var reviewedFilter = viewModel.TransactionHistoryFilters.Single(item =>
            item.State == MachineIntegrationTransactionHistoryState.Reviewed);
        viewModel.SelectedTransactionHistoryFilter = reviewedFilter;
        Assert.Single(viewModel.VisibleTransactionHistoryRows);
        Assert.Contains("Reviewed", viewModel.VisibleTransactionHistoryRows[0].StatusText, StringComparison.Ordinal);

        PublishPassResult(fixture.ExchangeRoot, handoff, consumer);
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.ResultStatusText.Contains("Run ", StringComparison.Ordinal));

        Assert.Same(reviewedFilter, viewModel.SelectedTransactionHistoryFilter);
        Assert.Empty(viewModel.VisibleTransactionHistoryRows);
        Assert.True(viewModel.HasTransactionHistoryEmptyState);
        var publishedFilter = viewModel.TransactionHistoryFilters.Single(item =>
            item.State == MachineIntegrationTransactionHistoryState.ResultPublished);
        viewModel.SelectedTransactionHistoryFilter = publishedFilter;
        Assert.Single(viewModel.VisibleTransactionHistoryRows);
        Assert.Contains("Result published", viewModel.VisibleTransactionHistoryRows[0].StatusText, StringComparison.Ordinal);

        File.WriteAllText(
            Path.Combine(transactionDirectory, IntegrationTransactionLayout.ResultFileName),
            "{ malformed-result }");
        viewModel.RefreshResultsCommand.Execute(null);
        await WaitForAsync(() =>
            !viewModel.IsBusy
            && viewModel.ResultStatusText.Contains("Result read failed", StringComparison.Ordinal));

        Assert.Same(publishedFilter, viewModel.SelectedTransactionHistoryFilter);
        Assert.Empty(viewModel.VisibleTransactionHistoryRows);
        var readFailedFilter = viewModel.TransactionHistoryFilters.Single(item =>
            item.State == MachineIntegrationTransactionHistoryState.ResultReadFailed);
        viewModel.SelectedTransactionHistoryFilter = readFailedFilter;
        Assert.Single(viewModel.VisibleTransactionHistoryRows);
        Assert.Contains("Result read failed", viewModel.VisibleTransactionHistoryRows[0].StatusText, StringComparison.Ordinal);

        viewModel.SelectedTransactionHistoryFilter = pendingFilter;
        Assert.Empty(viewModel.VisibleTransactionHistoryRows);
        Assert.Contains("No transaction history matches", viewModel.TransactionHistoryEmptyText, StringComparison.Ordinal);
        Assert.Equal(viewModel.TransactionHistory.Count, viewModel.TransactionHistoryRows.Count);

        viewModel.Setup.ResetSetupCommand.Execute(null);
        Assert.Null(viewModel.SelectedTransactionHistoryFilter?.State);
        Assert.Empty(viewModel.TransactionHistory);
        Assert.Empty(viewModel.TransactionHistoryRows);
        Assert.Empty(viewModel.VisibleTransactionHistoryRows);
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

        viewModel.Setup.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.Setup.InspectionRecipePath = fixture.RecipePath;
        viewModel.Setup.TwoDConsumerVersion = consumer.ApplicationVersion;
        viewModel.Setup.TwoDConsumerCommit = consumer.SourceCommit;
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

    private static SimulationSnapshot CreateSimulationSnapshot(VirtualCameraSnapshot camera) =>
        new(
            TimeSpan.FromMilliseconds(15),
            3,
            SimulationRunMode.Paused,
            SimulationControlOwner.Manual,
            1,
            Array.Empty<AxisSnapshot>(),
            0,
            Array.Empty<DigitalSignalSnapshot>(),
            Array.Empty<SequenceExecutionSnapshot>(),
            new[] { camera },
            AutomaticRunSnapshot.NotConfigured,
            Array.Empty<LayoutComponentSnapshot>(),
            projectId: "project-1",
            runtimeGeneration: 7);

    private static void PublishPassResult(
        string exchangeRoot,
        IntegrationHandoffV2 handoff,
        IntegrationApplicationIdentity consumer,
        bool includeProjection = false,
        bool includeImageEvidence = false)
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

        if (includeImageEvidence)
        {
            var imagePath = Path.Combine(
                transactionDirectory,
                IntegrationTransactionLayout.ArtifactsDirectoryName,
                "result-preview.png");
            var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
            File.WriteAllBytes(imagePath, imageBytes);
            evidence.Add(new IntegrationArtifactReference(
                IntegrationArtifactRoles.ResultEvidence,
                "result-preview",
                "artifacts/result-preview.png",
                imageBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(imageBytes))));
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
                TestStorage.RootPath,
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
            File.WriteAllBytes(ThreeDSourcePath, [0x43, 0x33, 0x44, 0x01, 0x02, 0x03]);
            File.WriteAllText(ThreeDRecipePath, "{\"tool\":\"height\"}", new UTF8Encoding(false));
        }

        private string Root { get; }
        public string ExchangeRoot { get; }
        public string RemoteExchangeRoot { get; }
        private string SourceRoot { get; }
        public string SettingsPath { get; }
        public string ProjectPath => Path.Combine(SourceRoot, "machine.ovmachine");
        public string SourcePath => Path.Combine(SourceRoot, "inspection-source.png");
        public string RecipePath => Path.Combine(SourceRoot, "inspection-recipe.json");
        public string ThreeDSourcePath => Path.Combine(SourceRoot, "inspection-height.c3d");
        public string ThreeDRecipePath => Path.Combine(SourceRoot, "inspection-height.recipe.json");

        public MachineIntegrationRequestContext CreateContext(string frameId = "frame-1", string acquisitionId = "acquisition-1")
        {
            var bytes = File.ReadAllBytes(SourcePath);
            var frame = new VirtualCameraFrameEvidence(frameId, "inspection-source.png",
                Convert.ToHexString(SHA256.HashData(bytes)), bytes.LongLength, 2, 2, "Mono8");
            return new MachineIntegrationRequestContext(
                true, "project-1", "machine-project/1.0",
                [new SequenceDefinition
                {
                    Id = "sequence-001",
                    Steps = [new SequenceStepDefinition
                    {
                        Id = "inspect-step", Action = SequenceStepAction.TriggerCamera,
                        TargetId = "camera-virtual", Parameter = "recipe-a"
                    }]
                }],
                ProjectPath, "camera-virtual", "recipe-a",
                new VirtualCameraSnapshot("camera-virtual", "Camera", VirtualCameraState.FrameReady,
                    1, acquisitionId, "recipe-a", 0, 0, null, frame),
                new VirtualSingleImageSourceDefinition
                {
                    SourceRelativePath = frame.SourceRelativePath,
                    Width = frame.Width, Height = frame.Height, PixelFormat = frame.PixelFormat
                });
        }

        public IntegrationApplicationIdentity CreateProducer() => new(IntegrationApplicationIds.MachineStudio,
            "1.0.0", new string('1', 40), IntegrationSourceState.Clean);

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
            CreateRequest(
                recipePath,
                SourcePath,
                IntegrationInspectionModality.TwoD,
                IntegrationInspectionInputKind.Image,
                producer,
                consumer);

        public MachineInspectionHandoffRequest CreateRequest(
            string recipePath,
            string sourcePath,
            IntegrationInspectionModality modality,
            IntegrationInspectionInputKind inputKind,
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
