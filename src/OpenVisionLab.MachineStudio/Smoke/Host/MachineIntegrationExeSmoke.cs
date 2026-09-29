#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Smoke;

internal sealed class MachineIntegrationExeSmokeReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Role { get; init; } = "producer";
    public string Mode { get; init; } = string.Empty;
    public string? IntegrationProfilePath { get; init; }
    public string? TransactionId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ApplyInput { get; init; } = "command";
    public long? PendingTickIndex { get; init; }
    public long? ResultVisibleTickIndex { get; init; }
    public string? ResultDocumentSha256 { get; init; }
    public string? PendingScreenshotPath { get; init; }
    public string? PressedScreenshotPath { get; init; }
    public string? AppliedScreenshotPath { get; init; }
    public string? TimeoutScreenshotPath { get; init; }
    public string? LateScreenshotPath { get; init; }
    public string? CancellationScreenshotPath { get; init; }
    public bool AutomaticRunCompleted { get; init; }
    public bool AutomaticTimeoutObserved { get; init; }
    public bool AutomaticCancellationRequested { get; init; }
    public bool AutomaticCancellationAccepted { get; init; }
    public bool AutomaticCancelledResultObserved { get; init; }
    public bool DuplicateCancellationRequested { get; init; }
    public bool DuplicateCancellationAlreadyCancelled { get; init; }
    public int CancellationDelayMilliseconds { get; init; }
    public string? CancellationOrder { get; init; }
    public string? CancellationReceiptStatus { get; init; }
    public bool LateResultQuarantined { get; init; }
    public bool LateResultDidNotMutateRuntime { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class MachineIntegrationExeSmoke
{
    private const string RoleArgument = "--smoke-integration-exe-role";
    private const string ModeArgument = "--smoke-integration-exe-mode";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        string.Equals(
            GetArgumentValue(args, RoleArgument),
            "producer",
            StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var mode = GetArgumentValue(args, ModeArgument) ?? string.Empty;
        if (string.Equals(mode, "dual-order-3d-first", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "dual-order-2d-first", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "dual-concurrent", StringComparison.OrdinalIgnoreCase))
        {
            return await RunCrossModalOrderAsync(args);
        }

        var twoDMode = string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-delayed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-error", StringComparison.OrdinalIgnoreCase);
        var automaticTwoDExecutionErrorMode = string.Equals(
            mode,
            "automatic-2d-error",
            StringComparison.OrdinalIgnoreCase);
        var automaticThreeDExecutionErrorMode = string.Equals(
            mode,
            "automatic-3d-error",
            StringComparison.OrdinalIgnoreCase);
        var automaticThreeDTimeoutLateMode = string.Equals(
            mode,
            "automatic-3d-timeout-late",
            StringComparison.OrdinalIgnoreCase);
        var timeoutThenCancellationMode = args.Any(argument => string.Equals(
                argument,
                "--smoke-integration-cancel-after-timeout",
                StringComparison.OrdinalIgnoreCase))
            && (string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase)
                || automaticThreeDTimeoutLateMode);
        var cancelAtTimeoutDeadlineMode = args.Any(argument => string.Equals(
            argument,
            "--smoke-integration-cancel-at-timeout-deadline",
            StringComparison.OrdinalIgnoreCase));
        if (cancelAtTimeoutDeadlineMode && !timeoutThenCancellationMode)
        {
            throw new ArgumentException(
                "--smoke-integration-cancel-at-timeout-deadline requires automatic timeout cancellation mode.");
        }
        var expectedTimeoutKind = GetArgumentValue(
            args,
            "--smoke-integration-expected-timeout-kind");
        var automaticThreeDMode = automaticThreeDExecutionErrorMode || automaticThreeDTimeoutLateMode;
        var automaticExecutionErrorMode = automaticTwoDExecutionErrorMode || automaticThreeDExecutionErrorMode;
        var threeDMode = string.Equals(mode, "3d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "3d-cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "3d-cancelled-before-ack", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "3d-cancelled-before-run", StringComparison.OrdinalIgnoreCase)
            || automaticThreeDMode;
        var automaticMode = string.Equals(mode, "automatic-2d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-delayed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase)
            || automaticTwoDExecutionErrorMode
            || automaticThreeDMode;
        var delayedAutomaticMode = string.Equals(mode, "automatic-2d-delayed", StringComparison.OrdinalIgnoreCase)
            || (automaticThreeDTimeoutLateMode && !timeoutThenCancellationMode);
        var cancelledAutomaticMode = string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase);
        var cancelledThreeDMode = string.Equals(mode, "3d-cancelled", StringComparison.OrdinalIgnoreCase);
        var cancellationBeforeAcknowledgementMode = string.Equals(
            mode,
            "3d-cancelled-before-ack",
            StringComparison.OrdinalIgnoreCase);
        var cancellationBeforeRunMode = string.Equals(
            mode,
            "3d-cancelled-before-run",
            StringComparison.OrdinalIgnoreCase);
        var cancelledMode = cancelledAutomaticMode
            || cancelledThreeDMode
            || cancellationBeforeAcknowledgementMode
            || cancellationBeforeRunMode
            || timeoutThenCancellationMode;
        var expectsCancelledResult = cancelledAutomaticMode || cancelledThreeDMode || timeoutThenCancellationMode;
        var expectsExecutionErrorResult = automaticExecutionErrorMode;
        var reportPath = GetArgumentValue(args, "--smoke-integration-exe-report")
            ?? Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-integration-exe-smoke.json");
        var reportTarget = Path.GetFullPath(reportPath);
        var transactionId = (Guid?)null;
        var status = string.Empty;
        var applyInput = (GetArgumentValue(args, "--smoke-integration-apply-input") ?? "command")
            .ToLowerInvariant();
        var pendingScreenshotPath = GetArgumentValue(args, "--smoke-integration-pending-screenshot");
        var pressedScreenshotPath = GetArgumentValue(args, "--smoke-integration-pressed-screenshot");
        var appliedScreenshotPath = GetArgumentValue(args, "--smoke-integration-applied-screenshot");
        var timeoutScreenshotPath = GetArgumentValue(args, "--smoke-integration-timeout-screenshot");
        var lateScreenshotPath = GetArgumentValue(args, "--smoke-integration-late-screenshot");
        var cancellationScreenshotPath = GetArgumentValue(args, "--smoke-integration-cancellation-screenshot");
        var integrationProfilePath = GetArgumentValue(args, "--smoke-integration-profile");
        var holdMilliseconds = ParseMilliseconds(
            GetArgumentValue(args, "--smoke-integration-exe-hold-ms"),
            0,
            0,
            30000);
        var cancellationDelayMilliseconds = ParseMilliseconds(
            GetArgumentValue(args, "--smoke-integration-cancellation-delay-ms"),
            0,
            0,
            30000);
        var cancellationReadyFilePath = GetArgumentValue(
            args,
            "--smoke-integration-cancel-ready-file") is { Length: > 0 } readyPath
            ? Path.GetFullPath(readyPath)
            : null;
        var recoveryReadyFilePath = GetArgumentValue(
            args,
            "--smoke-integration-recovery-ready-file") is { Length: > 0 } recoveryReadyPath
            ? Path.GetFullPath(recoveryReadyPath)
            : null;
        var duplicateCancellationRequested = args.Any(argument =>
            string.Equals(argument, "--smoke-integration-cancel-twice", StringComparison.OrdinalIgnoreCase));
        var pendingTickIndex = (long?)null;
        var resultVisibleTickIndex = (long?)null;
        var resultDocumentSha256 = (string?)null;
        var automaticRunCompleted = false;
        var automaticTimeoutObserved = false;
        var automaticCancellationRequested = false;
        var automaticCancellationAccepted = false;
        var automaticCancelledResultObserved = false;
        var cancellationReceiptStatus = (string?)null;
        var duplicateCancellationWasRequested = false;
        var duplicateCancellationAlreadyCancelled = false;
        var lateResultQuarantined = false;
        var lateResultDidNotMutateRuntime = false;
        var timeoutTickIndex = (long?)null;
        Task<TcpIntegrationCancellationReceipt>? scheduledCancellationTask = null;
        SmokeWindowCapture? automaticCapture = null;
        MainViewModel? viewModel = null;
        ShellWindow? window = null;
        MachineIntegrationTcpExchange? directTransport = null;
        SmokeMonitorEvidence? monitor = null;
        SmokeNativeInput? nativeInput = null;

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed && !failures.Contains(name, StringComparer.Ordinal))
            {
                failures.Add(name);
            }
        }

        bool CancellationReadyFileMatches(Guid expectedTransactionId)
        {
            if (cancellationReadyFilePath is null || !File.Exists(cancellationReadyFilePath))
            {
                return false;
            }

            try
            {
                return Guid.TryParse(
                        File.ReadAllText(cancellationReadyFilePath).Trim(),
                        out var readyTransactionId)
                    && readyTransactionId == expectedTransactionId;
            }
            catch (IOException)
            {
                return false;
            }
        }

        bool RecoveryReadyFileExists() => recoveryReadyFilePath is not null
            && File.Exists(recoveryReadyFilePath);

        async Task<TcpIntegrationCancellationReceipt> SendCancellationAtDeadlineAsync(
            MachineIntegrationTcpExchange transport,
            TcpIntegrationEndpoint endpoint,
            Guid targetTransactionId,
            DateTimeOffset deadlineUtc)
        {
            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }

            var cancellationHandoff = transport.ReadHandoff(targetTransactionId);
            var cancellationRequest = new IntegrationCancelRequestV2(
                IntegrationContractSchema.V2,
                IntegrationMessageKind.CancelRequest,
                Guid.NewGuid(),
                targetTransactionId,
                cancellationHandoff.MessageId,
                DateTimeOffset.UtcNow,
                cancellationHandoff.Producer,
                "Machine Studio smoke cancellation at automatic timeout deadline");
            return await transport.CancelTransactionAsync(endpoint, cancellationRequest);
        }

        try
        {
            if (!twoDMode
                && !threeDMode)
            {
                throw new ArgumentException("Machine integration EXE smoke mode must be '2d', 'automatic-2d', 'automatic-2d-delayed', 'automatic-2d-cancelled', 'automatic-2d-error', 'automatic-3d-error', 'automatic-3d-timeout-late', '3d', '3d-cancelled', '3d-cancelled-before-ack', or '3d-cancelled-before-run'.");
            }
            if (applyInput is not ("command" or "pointer" or "keyboard"))
            {
                throw new ArgumentException(
                    "Machine integration EXE smoke apply input must be 'command', 'pointer', or 'keyboard'.");
            }

            var machineRoot = RequireArgument(args, "--smoke-integration-machine-root");
            var consumerRoot = RequireArgument(args, "--smoke-integration-consumer-root");
            var projectPath = RequireArgument(args, "--smoke-integration-project");
            var recipePath = RequireArgument(args, "--smoke-integration-recipe");
            var settingsPath = RequireArgument(args, "--smoke-integration-settings");
            var heightMapSourcePath = threeDMode
                ? RequireArgument(args, "--smoke-integration-source")
                : null;
            var heightMapWidth = ParsePositiveInteger(
                GetArgumentValue(args, "--smoke-integration-heightmap-width"),
                1280);
            var heightMapHeight = ParsePositiveInteger(
                GetArgumentValue(args, "--smoke-integration-heightmap-height"),
                840);
            var consumerVersion = RequireValue(args, "--smoke-integration-consumer-version");
            var consumerCommit = RequireValue(args, "--smoke-integration-consumer-commit");
            var listenPort = ParsePort(
                GetArgumentValue(args, "--smoke-integration-listen-port"),
                45101);
            var peerPort = ParsePort(
                GetArgumentValue(args, "--smoke-integration-peer-port"),
                twoDMode
                    ? 45102
                    : 45103);
            var sharedKey = ReadSharedKey();
            Directory.CreateDirectory(machineRoot);
            Directory.CreateDirectory(consumerRoot);

            if (!string.IsNullOrWhiteSpace(integrationProfilePath))
            {
                Check("integrationProfilePresent", File.Exists(Path.GetFullPath(integrationProfilePath)));
                if (File.Exists(Path.GetFullPath(integrationProfilePath)))
                {
                    try
                    {
                        using var profile = JsonDocument.Parse(File.ReadAllText(integrationProfilePath));
                        var profileRoot = profile.RootElement;
                        Check("integrationProfileSchema", profileRoot.GetProperty("schemaVersion").GetInt32() == 1);
                        Check("integrationProfileExchangeRoot",
                            string.Equals(
                                profileRoot.GetProperty("machineExchangeRoot").GetString(),
                                Path.GetFullPath(machineRoot),
                                StringComparison.OrdinalIgnoreCase));
                        Check("integrationProfileMachineListenPort",
                            profileRoot.GetProperty("machine").GetProperty("listenPort").GetInt32() == listenPort);
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
                    {
                        Check("integrationProfileSchema", false);
                    }
                }
            }

            var project = new ProjectDocumentStore().Load(File.ReadAllText(projectPath));
            viewModel = new MainViewModel(
                project,
                projectPath,
                integrationSettingsPath: settingsPath,
                automaticExternalInspectionWallTimeout: cancelAtTimeoutDeadlineMode
                    ? SimulationSettings.DefaultAutomaticExternalInspectionWallTimeout
                    : cancelledAutomaticMode
                    ? TimeSpan.FromSeconds(30)
                    : null);
            // The smoke host owns report persistence after the WPF window closes.
            // Keep shutdown explicit so OnLastWindowClose cannot terminate the
            // dispatcher before the finally block writes the report.
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new ShellWindow
            {
                DataContext = viewModel
            };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
            window.Show();
            monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
            Check("machineWindowOnTestMonitor", monitor.WindowIntersectsMonitor);
            Check("machineWindowContainedOnTestMonitor", monitor.WindowContainedByMonitor);

            viewModel.IsRunMode = true;
            viewModel.Navigation.IsInspectionWorkspace = true;
            viewModel.Navigation.SelectedInspectionTabIndex = 1;
            Check("machineRunModeEnabled", viewModel.IsRunMode);
            ConfigureMachineIntegration(
                viewModel.Integration,
                machineRoot,
                recipePath,
                consumerVersion,
                consumerCommit,
                listenPort,
                peerPort,
                sharedKey,
                mode,
                heightMapSourcePath,
                heightMapWidth,
                heightMapHeight);
            Check("machineIntegrationConfigured", true);
            Check("machineSettingsCanBeSaved", viewModel.Integration.Setup.SaveSetupCommand.CanExecute(null));
            viewModel.Integration.Setup.SaveSetupCommand.Execute(null);
            await Task.Delay(100);
            Check("machineSettingsSaved", File.Exists(settingsPath));

            await viewModel.Integration.StartTcpListenerAsync();
            Check("machineListening", viewModel.Integration.IsTcpListening);

            directTransport = new MachineIntegrationTcpExchange(machineRoot, sharedKey);
            var consumerEndpoint = new TcpIntegrationEndpoint("127.0.0.1", peerPort);
            var consumerPingAccepted = false;
            if (twoDMode || threeDMode)
            {
                await WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(consumerEndpoint);
                    consumerPingAccepted = receipt.PeerApplicationId == (twoDMode
                        ? IntegrationApplicationIds.TwoDStudio
                        : IntegrationApplicationIds.ThreeDStudio);
                            return consumerPingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    twoDMode
                        ? "The 2D consumer did not become reachable before the automatic run."
                        : "The 3D consumer did not become reachable before the automatic run.");
                Check("consumerPingAcceptedBeforeRun", consumerPingAccepted);
            }
            if (twoDMode)
            {
                if (automaticMode)
                {
                    Check("automaticRunConfigured",
                        project.Simulation.AutomaticRun is not null
                        && !string.IsNullOrWhiteSpace(project.Simulation.AutomaticRun.SequenceId));
                    Check("automaticRunWaitEnabled", viewModel.Integration.Setup.WaitForExternalResult);
                    await WaitForAsync(
                        () => viewModel.RunCommand.CanExecute(null),
                        TimeSpan.FromSeconds(10),
                        "Automatic Machine Run command did not become ready after WPF initialization.");
                    Check("automaticRunCommandAvailable", viewModel.RunCommand.CanExecute(null));
                    viewModel.RunCommand.Execute(null);
                    await WaitForAsync(
                        () => MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                            .Any(item => item.Handoff.Context.Modality == IntegrationInspectionModality.TwoD),
                        TimeSpan.FromSeconds(120),
                        "Automatic Machine run did not publish a 2D transaction.");
                    var automaticPublished = MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                        .Where(item => item.Handoff.Context.Modality == IntegrationInspectionModality.TwoD)
                        .OrderByDescending(item => item.Handoff.CreatedAtUtc)
                        .First();
                    transactionId = automaticPublished.Handoff.TransactionId;
                    if (cancelAtTimeoutDeadlineMode)
                    {
                        scheduledCancellationTask = SendCancellationAtDeadlineAsync(
                            directTransport ?? throw new InvalidOperationException("Machine TCP transport was unavailable."),
                            consumerEndpoint,
                            transactionId.Value,
                            automaticPublished.Handoff.CreatedAtUtc
                                + SimulationSettings.DefaultAutomaticExternalInspectionWallTimeout);
                    }
                    Check("automaticHandoffPublished", true);
                    Check("automaticTransactionPushed", File.Exists(Path.Combine(
                        machineRoot,
                        "transactions",
                        transactionId.Value.ToString("D"),
                        "handoff.json")));
                    await WaitForAsync(
                        () => viewModel.Integration.HasLatestTwoDImage,
                        TimeSpan.FromSeconds(30),
                        "Machine did not load the 2D image artifact before the pending inspection capture.");
                    await window!.Dispatcher.InvokeAsync(
                        () => window.UpdateLayout(),
                        DispatcherPriority.Render);
                    if (!string.IsNullOrWhiteSpace(pendingScreenshotPath))
                    {
                        automaticCapture = new SmokeWindowCapture();
                        automaticCapture.Capture(
                            window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                            pendingScreenshotPath);
                        Check("pendingScreenshotCaptured", File.Exists(Path.GetFullPath(pendingScreenshotPath)));
                    }
                }
                else
                {
                    await MachineIntegrationExeSmokeCameraScenario.PrepareAsync(viewModel);
                }
                var camera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                if (!automaticMode)
                {
                    Check("cameraAwaitingExternalResult", camera?.State == VirtualCameraState.AwaitingExternalResult);
                    Check("cameraHasNoPlaceholderResult", camera?.Result is null && camera?.ExternalResultEvidence is null);
                }
                Check("cameraFrameIdentityAvailable", !string.IsNullOrWhiteSpace(viewModel.CurrentCameraFrameHashText));
                Check("machineBuildExactCommit", BuildIdentity.IsExactCommit);
                Check(
                    "machineProjectPathAvailable",
                    viewModel.CurrentProjectPath is { Length: > 0 } currentProjectPath
                    && File.Exists(currentProjectPath));
                Check("cameraAcquisitionAvailable", camera?.CurrentAcquisitionId is { Length: > 0 });
                if (!delayedAutomaticMode)
                {
                    Check(
                        "cameraFrameEvidenceAvailable",
                        camera?.FrameEvidence is not null || camera?.Result?.FrameEvidence is not null);
                }
                Check(
                    "cameraSourceAvailable",
                    File.Exists(Path.Combine(
                        Path.GetDirectoryName(projectPath)!,
                        viewModel.CurrentCameraSourceText.Replace('/', Path.DirectorySeparatorChar))));
                Check(
                    "cameraTriggerStepConfigured",
                    project.Sequences
                        .SelectMany(sequence => sequence.Steps)
                        .Any(step => step.Action == SequenceStepAction.TriggerCamera
                            && string.Equals(step.TargetId, viewModel.SelectedCameraId, StringComparison.Ordinal)
                            && string.Equals(step.Parameter, viewModel.SelectedCameraRecipe, StringComparison.Ordinal)));
                Check("integrationNotBusy", automaticMode || !viewModel.Integration.IsBusy);
                Check("consumerIdentityAvailable", viewModel.Integration.Setup.TwoDConsumerIdentity is not null);
                Check("exchangeRootAvailable", Directory.Exists(viewModel.Integration.Setup.ExchangeRoot.Trim()));
                Check("integrationRecipeAvailable", File.Exists(viewModel.Integration.Setup.InspectionRecipePath.Trim()));
                if (!delayedAutomaticMode)
                {
                    Check(
                        "cameraFrameSourceMatches",
                        camera?.FrameEvidence is { } frame
                        && File.Exists(Path.Combine(
                            Path.GetDirectoryName(projectPath)!,
                            frame.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                        && frame.ContentLength == new FileInfo(Path.Combine(
                            Path.GetDirectoryName(projectPath)!,
                            frame.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar))).Length
                        && string.Equals(
                            frame.ContentSha256,
                            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(
                                Path.GetDirectoryName(projectPath)!,
                                frame.SourceRelativePath.Replace('/', Path.DirectorySeparatorChar))))),
                            StringComparison.OrdinalIgnoreCase));
                }
                try
                {
                    _ = BuildIdentity.IntegrationIdentity;
                    Check("machineIntegrationIdentityQualified", true);
                }
                catch (IntegrationContractException)
                {
                    Check("machineIntegrationIdentityQualified", false);
                }
                Check("cameraWorkflowReadyForPublish", automaticMode
                    ? transactionId is not null
                    : viewModel.Integration.CanPublishTwoDImageHandoff);

                await WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(consumerEndpoint);
                            consumerPingAccepted = receipt.PeerApplicationId == IntegrationApplicationIds.TwoDStudio;
                            return consumerPingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    "The 2D consumer did not accept a TCP Ping.");
                Check("consumerPingAccepted", consumerPingAccepted);

                if (automaticMode)
                {
                    Check("automaticConsumerPingAccepted", consumerPingAccepted);
                }
                else
                {
                    viewModel.Integration.PublishTwoDImageHandoffCommand.Execute(null);
                await WaitForAsync(
                    () => !viewModel.Integration.IsBusy,
                    TimeSpan.FromSeconds(60),
                    "Machine 2D Publish did not complete.");
                Check("twoDHandoffPublished", viewModel.Integration.CanPushLatestTransaction);
                var published = MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                    .OrderByDescending(item => item.Handoff.CreatedAtUtc)
                    .FirstOrDefault();
                if (published is null)
                {
                    throw new InvalidOperationException("Machine did not publish a 2D transaction.");
                }

                transactionId = published.Handoff.TransactionId;
                Check(
                    "publishedTwoDImageContract",
                    published.Handoff.Context.Modality == IntegrationInspectionModality.TwoD
                    && published.Handoff.Context.InputKind == IntegrationInspectionInputKind.Image
                    && published.Handoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.TwoDStudio);

                viewModel.Integration.PushLatestTransactionCommand.Execute(null);
                await WaitForAsync(
                    () => !viewModel.Integration.IsTcpBusy,
                    TimeSpan.FromSeconds(60),
                    "Machine 2D Push did not complete.");
                Check(
                    "twoDTransactionPushed",
                    viewModel.Integration.LastTcpTransferText.Contains(
                        "push",
                        StringComparison.OrdinalIgnoreCase));
                }
            }
            else if (automaticThreeDMode)
            {
                Check("automaticRunConfigured",
                    project.Simulation.AutomaticRun is not null
                    && !string.IsNullOrWhiteSpace(project.Simulation.AutomaticRun.SequenceId));
                Check("automaticThreeDSetupReady", viewModel.Integration.Setup.IsThreeDAutomaticSetupReady);
                Check("automaticRunWaitEnabled", viewModel.Integration.Setup.WaitForExternalResult);
                await WaitForAsync(
                    () => viewModel.RunCommand.CanExecute(null),
                    TimeSpan.FromSeconds(10),
                    "Automatic Machine 3D Run command did not become ready after WPF initialization.");
                Check("automaticRunCommandAvailable", viewModel.RunCommand.CanExecute(null));
                viewModel.RunCommand.Execute(null);
                await WaitForAsync(
                    () => MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                        .Any(item => item.Handoff.Context.Modality == IntegrationInspectionModality.ThreeD),
                    TimeSpan.FromSeconds(120),
                    "Automatic Machine run did not publish a 3D HeightMap transaction.");
                var automaticPublished = MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                    .Where(item => item.Handoff.Context.Modality == IntegrationInspectionModality.ThreeD)
                    .OrderByDescending(item => item.Handoff.CreatedAtUtc)
                    .First();
                transactionId = automaticPublished.Handoff.TransactionId;
                if (cancelAtTimeoutDeadlineMode)
                {
                    scheduledCancellationTask = SendCancellationAtDeadlineAsync(
                        directTransport ?? throw new InvalidOperationException("Machine TCP transport was unavailable."),
                        consumerEndpoint,
                        transactionId.Value,
                        automaticPublished.Handoff.CreatedAtUtc
                            + SimulationSettings.DefaultAutomaticExternalInspectionWallTimeout);
                }
                Check("automaticThreeDHandoffPublished", true);
                Check("automaticThreeDTransactionPushed", File.Exists(Path.Combine(
                    machineRoot,
                    "transactions",
                    transactionId.Value.ToString("D"),
                    "handoff.json")));
                Check("automaticThreeDHeightMapContract",
                    automaticPublished.Handoff.Context.Modality == IntegrationInspectionModality.ThreeD
                    && automaticPublished.Handoff.Context.InputKind == IntegrationInspectionInputKind.HeightMap
                    && automaticPublished.Handoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.ThreeDStudio);
            }
            else
            {
                var consumer = CreateConsumerIdentity(
                    IntegrationApplicationIds.ThreeDStudio,
                    consumerVersion,
                    consumerCommit);
                var request = new MachineInspectionHandoffRequest(
                    project.Id,
                    "1.0",
                    "cross-repo-3d-smoke-sequence",
                    "inspect-heightmap",
                    "camera-virtual",
                    "cross-repo-3d-acquisition",
                    "frame.c3d-grid-index",
                    "raw-height",
                    projectPath,
                    RequireArgument(args, "--smoke-integration-source"),
                    recipePath,
                    IntegrationInspectionModality.ThreeD,
                    IntegrationInspectionInputKind.HeightMap,
                    BuildIdentity.IntegrationIdentity,
                    consumer);

                await WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(consumerEndpoint);
                            consumerPingAccepted = receipt.PeerApplicationId == IntegrationApplicationIds.ThreeDStudio;
                            return consumerPingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    "The 3D consumer did not accept a TCP Ping.");
                Check("consumerPingAccepted", consumerPingAccepted);

                var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
                    machineRoot,
                    request);
                transactionId = handoff.TransactionId;
                Check(
                    "publishedThreeDHeightMapContract",
                    handoff.Context.Modality == IntegrationInspectionModality.ThreeD
                    && handoff.Context.InputKind == IntegrationInspectionInputKind.HeightMap
                    && handoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.ThreeDStudio);
                var receipt = await directTransport.PushTransactionAsync(
                    consumerEndpoint,
                    handoff.TransactionId);
                Check(
                    "threeDTransactionPushed",
                    receipt.Operation.Equals("push", StringComparison.OrdinalIgnoreCase)
                    && receipt.PeerApplicationId == IntegrationApplicationIds.ThreeDStudio);
            }

            var id = transactionId
                ?? throw new InvalidOperationException("The Machine integration smoke has no transaction identity.");
            if (twoDMode)
            {
                await WaitForAsync(
                    () => viewModel.Integration.HasLatestTwoDImage,
                    TimeSpan.FromSeconds(30),
                    "Machine did not resolve the 2D consumer image artifact for the inspection viewer.");
                Check(
                    "twoDConsumerImageArtifactAvailable",
                    viewModel.Integration.LatestTwoDImageUri is { IsFile: true } imageUri
                    && File.Exists(imageUri.LocalPath));
            viewModel.Navigation.IsInspectionWorkspace = true;
            viewModel.Navigation.SelectedInspectionTabIndex = 1;
                await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                var mmi = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(window);
                var twoDImage = mmi is null
                    ? null
                    : SmokeVisualTreeQuery.FindVisualDescendant<Image>(
                        mmi,
                        candidate => string.Equals(candidate.Name, "MmiTwoDImageViewer", StringComparison.Ordinal));
                Check(
                    "twoDImageViewerRendered",
                    twoDImage is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Source: not null });
            }
            if (cancelledMode)
            {
                if (timeoutThenCancellationMode)
                {
                    if (cancelAtTimeoutDeadlineMode)
                    {
                        Check("cancellationScheduledAtTimeoutDeadline", scheduledCancellationTask is not null);
                    }
                    else
                    {
                        await WaitForAsync(
                            () =>
                            {
                                var timeoutCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                                var latestSnapshot = viewModel.SceneSnapshots.Latest;
                                return viewModel.OperationalDiagnostics.Any(item =>
                                        item.EventName == "AutomaticExternalInspectionTimedOut"
                                        || item.EventName == "AutomaticExternalInspectionFailedClosed")
                                    && timeoutCamera?.State == VirtualCameraState.Faulted
                                    && latestSnapshot?.RunMode == SimulationRunMode.Paused
                                    && latestSnapshot.AutomaticRun.IsActive == false;
                            },
                            TimeSpan.FromSeconds(30),
                            "Automatic Machine run did not fail closed before the delayed CancelRequest.");
                        var timedOutBeforeCancellation = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                        timeoutTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                        automaticTimeoutObserved = true;
                        Check("automaticTimeoutObservedBeforeCancellation", true);
                        Check(
                            "automaticRunFailedClosedBeforeCancellation",
                            timedOutBeforeCancellation?.State == VirtualCameraState.Faulted
                            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                            && viewModel.SceneSnapshots.Latest.AutomaticRun.IsActive == false);
                        Check("lateResultNotPublishedBeforeCancellation", !HasCompletedResult(machineRoot, id));
                        if (!string.IsNullOrWhiteSpace(timeoutScreenshotPath))
                        {
                            await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                            (automaticCapture ??= new SmokeWindowCapture()).Capture(
                                window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                                timeoutScreenshotPath);
                            Check("timeoutScreenshotCaptured", File.Exists(Path.GetFullPath(timeoutScreenshotPath)));
                        }
                    }
                }
                else if (cancellationDelayMilliseconds > 0)
                {
                    await Task.Delay(cancellationDelayMilliseconds);
                    Check("cancellationDelayApplied", true);
                }

                if (cancellationBeforeAcknowledgementMode
                    || cancellationBeforeRunMode
                    || cancelledThreeDMode)
                {
                    if (cancellationReadyFilePath is null)
                    {
                        throw new ArgumentException(
                            "Ordered 3D cancellation modes require '--smoke-integration-cancel-ready-file'.");
                    }

                    await WaitForAsync(
                        () => CancellationReadyFileMatches(id),
                        TimeSpan.FromSeconds(60),
                        "The 3D consumer did not signal the requested cancellation ordering boundary.");
                }

                if (!cancellationBeforeAcknowledgementMode && !cancelAtTimeoutDeadlineMode)
                {
                    await Task.Delay(100);
                    Check(
                        "refreshResultsCommandAvailableBeforeCancellation",
                        viewModel.Integration.RefreshResultsCommand.CanExecute(null));
                    var cancellationRefreshDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
                    while ((!viewModel.Integration.CanCancelLatestTransaction
                            || (cancellationBeforeRunMode && !CancellationReadyFileMatches(id)))
                        && DateTimeOffset.UtcNow < cancellationRefreshDeadline)
                    {
                        if (!viewModel.Integration.IsBusy
                            && viewModel.Integration.RefreshResultsCommand.CanExecute(null))
                        {
                            viewModel.Integration.RefreshResultsCommand.Execute(null);
                        }

                        await Task.Delay(250);
                    }
                    Check("cancelCommandAvailable", viewModel.Integration.CanCancelLatestTransaction);
                    if (!viewModel.Integration.CanCancelLatestTransaction)
                    {
                        throw new TimeoutException(
                            "Machine did not expose the accepted transaction cancellation command.");
                    }
                }

                automaticCancellationRequested = true;
                if (cancellationBeforeAcknowledgementMode)
                {
                    var cancellationHandoff = directTransport.ReadHandoff(id);
                    var cancellationRequest = new IntegrationCancelRequestV2(
                        IntegrationContractSchema.V2,
                        IntegrationMessageKind.CancelRequest,
                        Guid.NewGuid(),
                        id,
                        cancellationHandoff.MessageId,
                        DateTimeOffset.UtcNow,
                        cancellationHandoff.Producer,
                        "Machine Studio smoke cancellation before 3D acknowledgement");
                    var receipt = await directTransport.CancelTransactionAsync(
                        consumerEndpoint,
                        cancellationRequest);
                    cancellationReceiptStatus = receipt.Status.ToString();
                    Check(
                        "cancellationReceiptNotFound",
                        receipt.Status == TcpIntegrationCancellationStatus.NotFound);
                    Check("cancellationReceiptTargetsTransaction", receipt.TransactionId == id);
                }
                else if (cancelAtTimeoutDeadlineMode)
                {
                    var receipt = await (scheduledCancellationTask
                        ?? throw new InvalidOperationException("The same-deadline cancellation request was not scheduled."));
                    cancellationReceiptStatus = receipt.Status.ToString();
                    automaticCancellationAccepted = receipt.Status == TcpIntegrationCancellationStatus.Accepted;
                    Check("cancellationReceiptAccepted", automaticCancellationAccepted);
                    Check("cancellationReceiptTargetsTransaction", receipt.TransactionId == id);
                }
                else
                {
                    viewModel.Integration.CancelLatestTransactionCommand.Execute(null);
                    await WaitForAsync(
                        () => !viewModel.Integration.IsTcpBusy,
                        TimeSpan.FromSeconds(60),
                        "Machine cancellation request did not complete.");
                    cancellationReceiptStatus = viewModel.Integration.LastTcpTransferText;
                    if (cancellationBeforeRunMode)
                    {
                        Check(
                            "cancellationReceiptNotFound",
                            viewModel.Integration.LastTcpTransferText.Contains(
                                "NotFound",
                                StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        automaticCancellationAccepted = viewModel.Integration.LastTcpTransferText.Contains(
                            "Accepted",
                            StringComparison.OrdinalIgnoreCase);
                        Check("cancellationReceiptAccepted", automaticCancellationAccepted);
                    }

                    Check(
                        "cancellationReceiptTargetsTransaction",
                        viewModel.Integration.LastTcpTransferText.Contains(
                            id.ToString("D"),
                            StringComparison.OrdinalIgnoreCase));
                }
                if (cancelAtTimeoutDeadlineMode)
                {
                    await WaitForAsync(
                        () =>
                        {
                            var timeoutCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                            var latestSnapshot = viewModel.SceneSnapshots.Latest;
                            return viewModel.OperationalDiagnostics.Any(item =>
                                    item.EventName == "AutomaticExternalInspectionTimedOut"
                                    || item.EventName == "AutomaticExternalInspectionFailedClosed")
                                && timeoutCamera?.State == VirtualCameraState.Faulted
                                && latestSnapshot?.RunMode == SimulationRunMode.Paused
                                && latestSnapshot.AutomaticRun.IsActive == false;
                        },
                        TimeSpan.FromSeconds(30),
                        "Automatic Machine timeout did not win the same-deadline cancellation race.");
                    var timedOutAtCancellationDeadline = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                    timeoutTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                    automaticTimeoutObserved = true;
                    Check("automaticTimeoutObservedAtCancellationDeadline", true);
                    Check(
                        "automaticRunFailedClosedAtCancellationDeadline",
                        timedOutAtCancellationDeadline?.State == VirtualCameraState.Faulted
                        && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                        && viewModel.SceneSnapshots.Latest.AutomaticRun.IsActive == false);
                    Check("lateResultNotPublishedAtCancellationDeadline", !HasCompletedResult(machineRoot, id));
                    if (!string.IsNullOrWhiteSpace(timeoutScreenshotPath))
                    {
                        await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                        (automaticCapture ??= new SmokeWindowCapture()).Capture(
                            window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                            timeoutScreenshotPath);
                        Check("timeoutScreenshotCaptured", File.Exists(Path.GetFullPath(timeoutScreenshotPath)));
                    }
                }
                Check(
                    "cancellationRequestBeforeResult",
                    timeoutThenCancellationMode
                        ? automaticTimeoutObserved
                        : !HasCompletedResult(machineRoot, id));
                if (duplicateCancellationRequested)
                {
                    duplicateCancellationWasRequested = true;
                    var duplicateCancellationTargetsTransaction = false;
                    if (timeoutThenCancellationMode)
                    {
                        var duplicateHandoff = directTransport.ReadHandoff(id);
                        var duplicateRequest = new IntegrationCancelRequestV2(
                            IntegrationContractSchema.V2,
                            IntegrationMessageKind.CancelRequest,
                            Guid.NewGuid(),
                            id,
                            duplicateHandoff.MessageId,
                            DateTimeOffset.UtcNow,
                            duplicateHandoff.Producer,
                            "Machine Studio smoke duplicate cancellation after timeout");
                        var duplicateReceipt = await directTransport.CancelTransactionAsync(
                            consumerEndpoint,
                            duplicateRequest);
                        duplicateCancellationAlreadyCancelled =
                            duplicateReceipt.Status == TcpIntegrationCancellationStatus.AlreadyCancelled;
                        duplicateCancellationTargetsTransaction = duplicateReceipt.TransactionId == id;
                    }
                    else
                    {
                        await WaitForAsync(
                            () => !viewModel.Integration.IsTcpBusy
                                && viewModel.Integration.CancelLatestTransactionCommand.CanExecute(null),
                            TimeSpan.FromSeconds(30),
                            "Machine did not retain the duplicate cancellation command gate.");
                        viewModel.Integration.CancelLatestTransactionCommand.Execute(null);
                        await WaitForAsync(
                            () => !viewModel.Integration.IsTcpBusy,
                            TimeSpan.FromSeconds(60),
                            "Machine duplicate cancellation request did not complete.");
                        duplicateCancellationAlreadyCancelled = viewModel.Integration.LastTcpTransferText.Contains(
                            "AlreadyCancelled",
                            StringComparison.OrdinalIgnoreCase);
                        duplicateCancellationTargetsTransaction = viewModel.Integration.LastTcpTransferText.Contains(
                            id.ToString("D"),
                            StringComparison.OrdinalIgnoreCase);
                    }
                    Check(
                        "duplicateCancellationReceiptAlreadyCancelled",
                        duplicateCancellationAlreadyCancelled);
                    Check(
                        "duplicateCancellationReceiptTargetsTransaction",
                        duplicateCancellationTargetsTransaction);
                }
                if (!string.IsNullOrWhiteSpace(cancellationScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        cancellationScreenshotPath);
                    Check(
                        "cancellationScreenshotCaptured",
                        File.Exists(Path.GetFullPath(cancellationScreenshotPath)));
                }
            }
            if (delayedAutomaticMode)
            {
                await WaitForAsync(
                    () =>
                    {
                        var currentCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                        return currentCamera?.State == VirtualCameraState.Faulted
                            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                            && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false
                            && viewModel.OperationalDiagnostics.Any(item =>
                                item.EventName == "AutomaticExternalInspectionTimedOut"
                                || item.EventName == "AutomaticExternalInspectionFailedClosed");
                    },
                    TimeSpan.FromSeconds(30),
                    "Automatic Machine run did not fail closed before the delayed consumer Result.");
                var timedOutCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                timeoutTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                automaticTimeoutObserved = true;
                Check("automaticTimeoutObserved", true);
                Check(
                    "cameraFrameEvidenceReleasedAfterTimeout",
                    timedOutCamera?.State == VirtualCameraState.Faulted
                    && timedOutCamera.FrameEvidence is null
                    && timedOutCamera.Result?.FrameEvidence is null);
                Check(
                    "cameraFrameSourceReleasedAfterTimeout",
                    timedOutCamera?.State == VirtualCameraState.Faulted
                    && timedOutCamera.FrameEvidence is null);
                Check(
                    "automaticRunFailedClosed",
                    timedOutCamera?.State == VirtualCameraState.Faulted
                    && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                    && viewModel.SceneSnapshots.Latest.AutomaticRun.IsActive == false);
                if (!string.IsNullOrWhiteSpace(expectedTimeoutKind))
                {
                    Check(
                        "automaticTimeoutKindObserved",
                        viewModel.OperationalDiagnostics.Any(item =>
                            item.EventName == "AutomaticExternalInspectionTimedOut"
                            && item.Message.Contains(
                                $"timeoutKind={expectedTimeoutKind}",
                                StringComparison.OrdinalIgnoreCase)));
                }
                Check("lateResultNotPublishedBeforeTimeout", !HasCompletedResult(machineRoot, id));
                if (!string.IsNullOrWhiteSpace(timeoutScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        timeoutScreenshotPath);
                    Check("timeoutScreenshotCaptured", File.Exists(Path.GetFullPath(timeoutScreenshotPath)));
                }
            }
            await WaitForAsync(
                () => HasCompletedResult(machineRoot, id),
                TimeSpan.FromSeconds(120),
                "Machine did not receive a completed consumer Result.");
            var resultBeforePull = MachineIntegrationExchange.ReadResult(machineRoot, id);
            if (expectsCancelledResult)
            {
                Check(
                    "consumerResultCancelled",
                    resultBeforePull.Status == IntegrationResultStatus.Cancelled
                    && resultBeforePull.Outcome == IntegrationInspectionOutcome.Indeterminate
                    && resultBeforePull.Error?.Code == IntegrationErrorCode.Cancelled);
            }
            else if (expectsExecutionErrorResult)
            {
                Check(
                    "consumerResultExecutionError",
                    resultBeforePull.Status == IntegrationResultStatus.Failed
                    && resultBeforePull.Outcome == IntegrationInspectionOutcome.ExecutionError
                    && resultBeforePull.RunId is null
                    && resultBeforePull.RunRecord is null
                    && resultBeforePull.Evidence.Count == 0
                    && resultBeforePull.Error?.Code == IntegrationErrorCode.ExecutionFailed);
            }
            else
            {
                Check(
                    "consumerResultCompletedPass",
                    resultBeforePull.Status == IntegrationResultStatus.Completed
                    && resultBeforePull.Outcome == IntegrationInspectionOutcome.Pass
                    && !string.IsNullOrWhiteSpace(resultBeforePull.RunId));
            }

            if (automaticThreeDExecutionErrorMode)
            {
                Check("resultAlreadyPushedFromConsumer", HasCompletedResult(machineRoot, id));
            }
            else
            {
                var pullReceipt = await directTransport.PullTransactionAsync(
                    consumerEndpoint,
                    id);
                Check(
                    "resultPulledFromConsumer",
                    pullReceipt.Operation.Equals("pull", StringComparison.OrdinalIgnoreCase)
                    && pullReceipt.TransactionId == id);
            }

            if (viewModel.Integration.RefreshResultsCommand.CanExecute(null))
            {
                viewModel.Integration.RefreshResultsCommand.Execute(null);
                await WaitForAsync(
                    () => !viewModel.Integration.IsBusy,
                    TimeSpan.FromSeconds(30),
                    "Machine Result refresh did not complete.");
            }
            if (expectsExecutionErrorResult)
            {
                await WaitForAsync(
                    () => (threeDMode
                            ? viewModel.Integration.LatestThreeDResultStatusText
                            : viewModel.Integration.LatestTwoDResultStatusText)
                        .Contains("Failed", StringComparison.OrdinalIgnoreCase)
                        && (threeDMode
                            ? viewModel.Integration.LatestThreeDResultStatusText
                            : viewModel.Integration.LatestTwoDResultStatusText)
                        .Contains("ExecutionError", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(30),
                    "Machine did not project the execution-error Result status.");
            }
            else if (threeDMode && !automaticThreeDTimeoutLateMode)
            {
                await WaitForAsync(
                    () => expectsCancelledResult
                        ? viewModel.Integration.LatestThreeDResultStatusText.Contains(
                                "Cancelled",
                                StringComparison.OrdinalIgnoreCase)
                            && viewModel.Integration.LatestThreeDResultStatusText.Contains(
                                "Indeterminate",
                                StringComparison.OrdinalIgnoreCase)
                        : viewModel.Integration.LatestThreeDResultStatusText.Contains(
                                "Pass",
                                StringComparison.OrdinalIgnoreCase)
                            && viewModel.Integration.LatestThreeDResultStatusText.Contains(
                                "Completed",
                                StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(30),
                    expectsCancelledResult
                        ? "Machine did not project the cancelled 3D Result status."
                        : "Machine did not project the completed 3D Result status.");
            }
            var displayedResultStatus = threeDMode
                ? viewModel.Integration.LatestThreeDResultStatusText
                : delayedAutomaticMode || cancelledAutomaticMode || timeoutThenCancellationMode
                    ? viewModel.Integration.LatestTwoDResultStatusText
                    : automaticTwoDExecutionErrorMode
                        ? viewModel.Integration.LatestTwoDResultStatusText
                        : viewModel.Integration.ResultStatusText;
            if (delayedAutomaticMode && !automaticThreeDTimeoutLateMode)
            {
                await WaitForAsync(
                    () => viewModel.Integration.LatestTwoDResultStatusText.Contains(
                            "Pass",
                            StringComparison.OrdinalIgnoreCase)
                        && viewModel.Integration.LatestTwoDResultStatusText.Contains(
                            "Completed",
                            StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(30),
                    "Machine did not project the completed late 2D Result status before quarantine verification.");
                displayedResultStatus = viewModel.Integration.LatestTwoDResultStatusText;
            }
            else if (automaticThreeDTimeoutLateMode && !timeoutThenCancellationMode)
            {
                await WaitForAsync(
                    () => viewModel.Integration.LatestThreeDResultStatusText.Contains(
                            "Pass",
                            StringComparison.OrdinalIgnoreCase)
                        && viewModel.Integration.LatestThreeDResultStatusText.Contains(
                            "Completed",
                            StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(30),
                    "Machine did not project the completed late 3D Result status before quarantine verification.");
                displayedResultStatus = viewModel.Integration.LatestThreeDResultStatusText;
            }
            if (cancelledAutomaticMode && !timeoutThenCancellationMode)
            {
                await WaitForAsync(
                    () => viewModel.Integration.LatestTwoDResultStatusText.Contains(
                            "Cancelled",
                            StringComparison.OrdinalIgnoreCase)
                        && viewModel.Integration.LatestTwoDResultStatusText.Contains(
                            "Indeterminate",
                            StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(30),
                    "Machine did not project the cancelled 2D Result status.");
                displayedResultStatus = viewModel.Integration.LatestTwoDResultStatusText;
            }
            else if (timeoutThenCancellationMode)
            {
                await WaitForAsync(
                    () => (threeDMode
                            ? viewModel.Integration.LatestThreeDResultStatusText
                            : viewModel.Integration.LatestTwoDResultStatusText)
                        .Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
                        && (threeDMode
                            ? viewModel.Integration.LatestThreeDResultStatusText
                            : viewModel.Integration.LatestTwoDResultStatusText)
                        .Contains("Indeterminate", StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(30),
                    "Machine did not project the late cancelled Result status.");
                displayedResultStatus = threeDMode
                    ? viewModel.Integration.LatestThreeDResultStatusText
                    : viewModel.Integration.LatestTwoDResultStatusText;
            }
            Check(
                "machineResultDisplayed",
                expectsCancelledResult
                    ? displayedResultStatus.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
                        && displayedResultStatus.Contains("Indeterminate", StringComparison.OrdinalIgnoreCase)
                    : expectsExecutionErrorResult
                        ? displayedResultStatus.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                            && displayedResultStatus.Contains("ExecutionError", StringComparison.OrdinalIgnoreCase)
                    : displayedResultStatus.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                        && displayedResultStatus.Contains("Completed", StringComparison.OrdinalIgnoreCase));
            if (threeDMode
                && !automaticThreeDTimeoutLateMode
                && !expectsCancelledResult
                && !expectsExecutionErrorResult)
            {
                await WaitForAsync(
                    () => viewModel.Integration.HasLatestThreeDImage,
                    TimeSpan.FromSeconds(30),
                    "Machine did not resolve the 3D ResultEvidence image artifact for the inspection viewer.");
                Check(
                    "threeDConsumerImageArtifactAvailable",
                    viewModel.Integration.LatestThreeDImageUri is { IsFile: true } imageUri
                    && File.Exists(imageUri.LocalPath));
            viewModel.Navigation.IsInspectionWorkspace = true;
            viewModel.Navigation.SelectedInspectionTabIndex = 1;
                await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                var mmi = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(window);
                var threeDImage = mmi is null
                    ? null
                    : SmokeVisualTreeQuery.FindVisualDescendant<Image>(
                        mmi,
                        candidate => string.Equals(candidate.Name, "MmiThreeDImageViewer", StringComparison.Ordinal));
                Check(
                    "threeDImageViewerRendered",
                    threeDImage is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Source: not null });
                if (!string.IsNullOrWhiteSpace(appliedScreenshotPath))
                {
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        appliedScreenshotPath);
                    Check("appliedScreenshotCaptured", File.Exists(Path.GetFullPath(appliedScreenshotPath)));
                }
            }
            else if (cancelledAutomaticMode && !timeoutThenCancellationMode)
            {
                await WaitForAsync(
                    () =>
                    {
                        var cancelledCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                        return cancelledCamera?.State == VirtualCameraState.Faulted
                            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                            && viewModel.OperationalDiagnostics.Any(item =>
                                item.EventName == "AutomaticExternalInspectionFailedClosed");
                    },
                    TimeSpan.FromSeconds(30),
                    "Automatic Machine run did not fail closed after the cancelled 2D Result.");
                var cancelledCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                automaticCancelledResultObserved = cancelledCamera?.State == VirtualCameraState.Faulted
                    && cancelledCamera.Result is null
                    && cancelledCamera.ExternalResultEvidence?.TransactionId == id
                    && cancelledCamera.ExternalResultEvidence.Status == ExternalInspectionResultStatus.Cancelled;
                Check("automaticCancelledResultApplied", automaticCancelledResultObserved);
                Check("automaticRunFailedClosedAfterCancellation",
                    cancelledCamera?.State == VirtualCameraState.Faulted
                    && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                    && viewModel.OperationalDiagnostics.Any(item =>
                        item.EventName == "AutomaticExternalInspectionFailedClosed"));
                if (!string.IsNullOrWhiteSpace(cancellationScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        cancellationScreenshotPath);
                    Check(
                        "cancellationScreenshotCapturedAfterResult",
                        File.Exists(Path.GetFullPath(cancellationScreenshotPath)));
                }
            }
            else if (timeoutThenCancellationMode)
            {
                var lateCancelledCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                await WaitForAsync(
                    () => viewModel.OperationalDiagnostics.Any(item =>
                        item.EventName == "AutomaticExternalInspectionLateResultQuarantined"),
                    TimeSpan.FromSeconds(30),
                    "Machine did not quarantine the cancelled Result after timeout.");
                lateResultQuarantined = viewModel.OperationalDiagnostics.Any(item =>
                    item.EventName == "AutomaticExternalInspectionLateResultQuarantined");
                var lateCancelledTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                lateResultDidNotMutateRuntime = lateCancelledCamera?.State == VirtualCameraState.Faulted
                    && lateCancelledCamera.Result is null
                    && lateCancelledCamera.ExternalResultEvidence is null
                    && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                    && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false
                    && (!timeoutTickIndex.HasValue || timeoutTickIndex == lateCancelledTickIndex);
                Check("lateCancelledResultQuarantined", lateResultQuarantined);
                Check("lateCancelledResultDidNotMutateRuntime", lateResultDidNotMutateRuntime);
                Check(
                    "automaticRunRemainsClosedAfterLateCancellation",
                    lateCancelledCamera?.State == VirtualCameraState.Faulted
                    && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false);
                if (!string.IsNullOrWhiteSpace(lateScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        lateScreenshotPath);
                    Check("lateScreenshotCapturedAfterCancellation", File.Exists(Path.GetFullPath(lateScreenshotPath)));
                }
            }
            else if (automaticExecutionErrorMode)
            {
                var executionErrorCheckPrefix = automaticThreeDExecutionErrorMode
                    ? "automaticThreeD"
                    : "automaticTwoD";
                var executionErrorModality = automaticThreeDExecutionErrorMode ? "3D" : "2D";
                var recoveryModality = automaticThreeDExecutionErrorMode
                    ? IntegrationInspectionModality.ThreeD
                    : IntegrationInspectionModality.TwoD;
                var automaticSequenceId = project.Simulation.AutomaticRun?.SequenceId
                    ?? throw new InvalidOperationException(
                        $"Automatic {executionErrorModality} smoke project has no sequence binding.");
                if (!viewModel.StepCommand.CanExecute(null))
                {
                    throw new InvalidOperationException(
                        $"Machine Step command was unavailable after the automatic {executionErrorModality} ExecutionError Result.");
                }

                viewModel.StepCommand.Execute(null);
                await WaitForAsync(
                    () =>
                    {
                        var currentCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                        return currentCamera?.State == VirtualCameraState.Faulted
                            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                            && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false
                            && (automaticTwoDExecutionErrorMode
                                || viewModel.SceneSnapshots.Latest.ResetRetrySequenceId == automaticSequenceId)
                            && viewModel.SceneSnapshots.Latest.Sequences.Any(sequence =>
                                sequence.SequenceId == automaticSequenceId
                                && sequence.Status == SequenceExecutionStatus.Faulted);
                    },
                    TimeSpan.FromSeconds(60),
                    $"Machine did not fail closed after the {executionErrorModality} consumer ExecutionError Result.");
                var failedCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                Check(
                    $"{executionErrorCheckPrefix}ExecutionErrorApplied",
                    failedCamera?.State == VirtualCameraState.Faulted
                    && failedCamera.Result is null
                    && failedCamera.ExternalResultEvidence?.TransactionId == id
                    && failedCamera.ExternalResultEvidence.Status == ExternalInspectionResultStatus.Failed
                    && (automaticTwoDExecutionErrorMode
                        || viewModel.SceneSnapshots.Latest?.ResetRetrySequenceId == automaticSequenceId));
                if (!string.IsNullOrWhiteSpace(timeoutScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        timeoutScreenshotPath);
                    Check(
                        $"{executionErrorCheckPrefix}ExecutionErrorScreenshotCaptured",
                        File.Exists(Path.GetFullPath(timeoutScreenshotPath)));
                }
                if (recoveryReadyFilePath is not null)
                {
                    await WaitForAsync(
                        RecoveryReadyFileExists,
                        TimeSpan.FromSeconds(60),
                        $"The recovery {executionErrorModality} consumer did not become ready before Machine Reset and RetrySequence.");
                    Check($"recovery{executionErrorModality}ConsumerReady", true);
                }
                var failedRuntimeGeneration = viewModel.SceneSnapshots.Latest?.RuntimeGeneration;
                Check(
                    $"resetCommandAvailableAfterAutomatic{executionErrorModality}Error",
                    viewModel.ResetCommand.CanExecute(null));
                viewModel.ResetCommand.Execute(null);
                try
                {
                    await WaitForAsync(
                        () => viewModel.SceneSnapshots.Latest?.ResetRetrySequenceId == automaticSequenceId
                            && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                            && viewModel.SceneSnapshots.Latest?.Sequences.Any(sequence =>
                                sequence.SequenceId == automaticSequenceId
                                && sequence.Status == SequenceExecutionStatus.Ready) == true,
                        TimeSpan.FromSeconds(30),
                        "Machine Reset did not preserve the failed automatic sequence retry marker.");
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        $"{exception.Message} Last snapshot: {DescribeAutomaticRecoveryState(viewModel.SceneSnapshots.Latest, automaticSequenceId)}",
                        exception);
                }
                Check(
                    $"{executionErrorCheckPrefix}ResetAdvancedRuntime",
                    failedRuntimeGeneration.HasValue
                    && viewModel.SceneSnapshots.Latest?.RuntimeGeneration == failedRuntimeGeneration.Value + 1);
                Check($"retryCommandAvailableAfter{executionErrorModality}Reset", viewModel.RetrySequenceCommand.CanExecute(null));
                viewModel.RetrySequenceCommand.Execute(null);
                await WaitForAsync(
                    () => viewModel.SceneSnapshots.Latest?.ResetRetrySequenceId is null
                        && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == true,
                    TimeSpan.FromSeconds(30),
                    "Machine RetrySequence did not re-arm the automatic sequence after Reset.");
                Check(
                    $"{executionErrorCheckPrefix}RetryRearmedSequence",
                    viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == true
                    && viewModel.SceneSnapshots.Latest?.ResetRetrySequenceId is null);
                Check($"runCommandAvailableForAutomatic{executionErrorModality}Resume", viewModel.RunCommand.CanExecute(null));
                viewModel.RunCommand.Execute(null);
                await WaitForAsync(
                    () => MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                        .Any(item => item.Handoff.Context.Modality == recoveryModality
                            && item.Handoff.TransactionId != id),
                    TimeSpan.FromSeconds(120),
                    $"Machine did not publish a new {executionErrorModality} transaction after Reset and RetrySequence.");
                var retryPublished = MachineIntegrationExchange.DiscoverTransactions(machineRoot)
                    .Where(item => item.Handoff.Context.Modality == recoveryModality
                        && item.Handoff.TransactionId != id)
                    .OrderByDescending(item => item.Handoff.CreatedAtUtc)
                    .First();
                var failedTransactionId = id;
                transactionId = retryPublished.Handoff.TransactionId;
                Check($"{executionErrorCheckPrefix}RecoveryTransactionPublished", true);
                Check($"{executionErrorCheckPrefix}RecoveryUsesNewTransaction", transactionId != failedTransactionId);
                await WaitForAsync(
                    () => HasCompletedResult(machineRoot, transactionId.Value),
                    TimeSpan.FromSeconds(120),
                    $"Machine did not receive the successful {executionErrorModality} Result after Reset and RetrySequence.");
                var recoveryResult = MachineIntegrationExchange.ReadResult(machineRoot, transactionId.Value);
                Check(
                    $"{executionErrorCheckPrefix}RecoveryResultCompletedPass",
                    recoveryResult.Status == IntegrationResultStatus.Completed
                    && recoveryResult.Outcome == IntegrationInspectionOutcome.Pass
                    && !string.IsNullOrWhiteSpace(recoveryResult.RunId));
                Check($"{executionErrorCheckPrefix}RecoveryResultAlreadyPushed", HasCompletedResult(machineRoot, transactionId.Value));
                await RefreshIntegrationResultsAsync(
                    viewModel,
                    TimeSpan.FromSeconds(30),
                    $"Machine Result refresh did not complete after the automatic {executionErrorModality} recovery Result.");
                await WaitForAsync(
                    () => MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel)?.State
                            == VirtualCameraState.FrameReady
                        && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false,
                    TimeSpan.FromSeconds(60),
                    $"Automatic {executionErrorModality} sequence did not resume and complete after Reset and RetrySequence.");
                var recoveredCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                resultVisibleTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                resultDocumentSha256 = recoveredCamera?.ExternalResultEvidence?.ResultDocumentSha256;
                automaticRunCompleted = recoveredCamera?.State == VirtualCameraState.FrameReady
                    && recoveredCamera.Result?.Decision == PlaceholderInspectionDecision.Pass
                    && recoveredCamera.ExternalResultEvidence?.TransactionId == transactionId;
                Check($"{executionErrorCheckPrefix}RecoveryResultApplied", automaticRunCompleted);
                Check($"{executionErrorCheckPrefix}SequenceResumed", viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false);
                Check(
                    $"{executionErrorCheckPrefix}RecoveryExactDocumentHash",
                    string.Equals(
                        resultDocumentSha256,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                            Path.Combine(machineRoot, "transactions", transactionId.Value.ToString("D"), "result.json")))),
                        StringComparison.OrdinalIgnoreCase));
                displayedResultStatus = threeDMode
                    ? viewModel.Integration.LatestThreeDResultStatusText
                    : viewModel.Integration.LatestTwoDResultStatusText;
                Check(
                    $"{executionErrorCheckPrefix}RecoveryResultDisplayed",
                    displayedResultStatus.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                    && displayedResultStatus.Contains("Completed", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(appliedScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        appliedScreenshotPath);
                    Check($"{executionErrorCheckPrefix}RecoveryScreenshotCaptured", File.Exists(Path.GetFullPath(appliedScreenshotPath)));
                }
            }
            else if (automaticMode && !delayedAutomaticMode)
            {
                if (viewModel.Integration.RefreshResultsCommand.CanExecute(null))
                {
                    viewModel.Integration.RefreshResultsCommand.Execute(null);
                    await WaitForAsync(
                        () => !viewModel.Integration.IsBusy,
                        TimeSpan.FromSeconds(30),
                        "Automatic Machine Result refresh did not complete.");
                }

                await WaitForAsync(
                    () => MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel)?.State
                            == VirtualCameraState.FrameReady
                        && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false,
                    TimeSpan.FromSeconds(60),
                    "Automatic Machine run did not resume and complete after the external Result.");
                var automaticCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                resultVisibleTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                resultDocumentSha256 = automaticCamera?.ExternalResultEvidence?.ResultDocumentSha256;
                automaticRunCompleted = automaticCamera?.State == VirtualCameraState.FrameReady
                    && automaticCamera.Result?.Decision == PlaceholderInspectionDecision.Pass
                    && automaticCamera.ExternalResultEvidence?.TransactionId == id;
                Check("automaticResultApplied", automaticRunCompleted);
                Check("automaticSequenceResumed", viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false);
                Check("automaticResultExactDocumentHash",
                    string.Equals(
                        resultDocumentSha256,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                        Path.Combine(machineRoot, "transactions", id.ToString("D"), "result.json")))),
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(appliedScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    await Task.Delay(100);
                    var mmi = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(window);
                    var mmiResultStatus = mmi is null
                        ? null
                        : SmokeVisualTreeQuery.FindVisualDescendant<TextBlock>(
                            mmi,
                            candidate => string.Equals(
                                candidate.Name,
                                "MmiResultStatusTextBlock",
                                StringComparison.Ordinal));
                    Check(
                        "mmiResultStatusRendered",
                        mmiResultStatus is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
                        && mmiResultStatus.Text.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                        && mmiResultStatus.Text.Contains("Completed", StringComparison.OrdinalIgnoreCase));
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        appliedScreenshotPath);
                    Check("appliedScreenshotCaptured", File.Exists(Path.GetFullPath(appliedScreenshotPath)));
                }
            }
            else if (delayedAutomaticMode)
            {
                var lateCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                await WaitForAsync(
                    () => viewModel.OperationalDiagnostics.Any(item =>
                        item.EventName == "AutomaticExternalInspectionLateResultQuarantined"),
                    TimeSpan.FromSeconds(30),
                    "Machine did not publish the late-result quarantine diagnostic after receiving the delayed Result.");
                lateResultQuarantined = viewModel.OperationalDiagnostics.Any(item =>
                    item.EventName == "AutomaticExternalInspectionLateResultQuarantined");
                var lateTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                lateResultDidNotMutateRuntime = lateCamera?.State == VirtualCameraState.Faulted
                    && lateCamera.Result is null
                    && lateCamera.ExternalResultEvidence is null
                    && viewModel.SceneSnapshots.Latest?.RunMode == SimulationRunMode.Paused
                    && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false
                    && (!timeoutTickIndex.HasValue || timeoutTickIndex == lateTickIndex);
                Check("lateResultQuarantined", lateResultQuarantined);
                Check("lateResultDidNotMutateRuntime", lateResultDidNotMutateRuntime);
                Check("automaticRunRemainsClosedAfterLateResult",
                    lateCamera?.State == VirtualCameraState.Faulted
                    && viewModel.SceneSnapshots.Latest?.AutomaticRun.IsActive == false);
                if (!string.IsNullOrWhiteSpace(lateScreenshotPath))
                {
                    await window!.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    (automaticCapture ??= new SmokeWindowCapture()).Capture(
                        window ?? throw new InvalidOperationException("Machine integration window was unavailable."),
                        lateScreenshotPath);
                    Check("lateScreenshotCaptured", File.Exists(Path.GetFullPath(lateScreenshotPath)));
                }
            }
            else if (string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase))
            {
                var activeWindow = window
                    ?? throw new InvalidOperationException("Machine integration window was unavailable.");
                var pendingCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                pendingTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                Check("resultRefreshDidNotApplyAutomatically",
                    pendingCamera?.State == VirtualCameraState.AwaitingExternalResult
                    && pendingCamera.Result is null
                    && pendingCamera.ExternalResultEvidence is null);
                await WaitForAsync(
                    () => viewModel.Integration.CanApplyResultToSimulation
                        && viewModel.Integration.ApplyResultToSimulationCommand.CanExecute(null),
                    TimeSpan.FromSeconds(30),
                    "Machine explicit Result application did not become available after refresh.");
                Check("explicitApplyAvailable", viewModel.Integration.CanApplyResultToSimulation);

                var inspector = SmokeVisualTreeQuery.FindVisualDescendant<RightToolRegionView>(activeWindow)
                    ?? throw new InvalidOperationException("Machine integration Run inspector was unavailable.");
                var waitCheckBox = inspector.IntegrationWaitForExternalResultCheckBoxControl;
                waitCheckBox.BringIntoView();
                await activeWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("externalWaitOptionCheckedAndVisible",
                    waitCheckBox.IsChecked == true
                    && waitCheckBox.IsVisible
                    && waitCheckBox.ActualWidth > 0
                    && waitCheckBox.ActualHeight > 0);

            viewModel.Navigation.IsInspectionWorkspace = true;
            viewModel.Navigation.SelectedInspectionTabIndex = 1;
                await activeWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var integrationWorkspace = SmokeVisualTreeQuery.FindVisualDescendant<SimulationIntegrationWorkspaceView>(activeWindow)
                    ?? throw new InvalidOperationException("Machine integration workspace was unavailable.");
                var applyButton = integrationWorkspace.ApplyIntegrationResultToSimulationButtonControl;
                applyButton.BringIntoView();
                await activeWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("applyButtonEnabledAndVisible",
                    applyButton.IsEnabled
                    && applyButton.IsVisible
                    && applyButton.ActualWidth > 0
                    && applyButton.ActualHeight > 0);

                var capture = new SmokeWindowCapture();
                if (!string.IsNullOrWhiteSpace(pendingScreenshotPath))
                {
                    capture.Capture(activeWindow, pendingScreenshotPath);
                    Check("pendingScreenshotCaptured", File.Exists(Path.GetFullPath(pendingScreenshotPath)));
                }

                if (applyInput == "command")
                {
                    viewModel.Integration.ApplyResultToSimulationCommand.Execute(null);
                }
                else
                {
                    nativeInput = new SmokeNativeInput();
                    var interaction = CreateInteraction(activeWindow, nativeInput, capture);
                    await SmokeButtonPointerState.FocusAsync(
                        activeWindow,
                        applyButton,
                        interaction,
                        "Apply Result button did not receive keyboard focus.");
                    Check("applyButtonKeyboardFocused", applyButton.IsKeyboardFocused);
                    if (applyInput == "keyboard")
                    {
                        var inputSource = PresentationSource.FromVisual(applyButton)
                            ?? throw new InvalidOperationException(
                                "Apply Result button had no presentation source.");
                        applyButton.RaiseEvent(new KeyEventArgs(
                            Keyboard.PrimaryDevice,
                            inputSource,
                            Environment.TickCount,
                            Key.Space)
                        {
                            RoutedEvent = Keyboard.KeyDownEvent
                        });
                        applyButton.RaiseEvent(new KeyEventArgs(
                            Keyboard.PrimaryDevice,
                            inputSource,
                            Environment.TickCount,
                            Key.Space)
                        {
                            RoutedEvent = Keyboard.KeyUpEvent
                        });
                    }
                    else
                    {
                        await SmokeButtonPointerState.HoverThenPressAsync(
                            activeWindow,
                            applyButton,
                            interaction,
                            () => "Apply Result button did not enter hover state.",
                            "Apply Result button did not enter pointer-down state.");
                        Check("applyButtonHover", applyButton.IsMouseOver);
                        Check("applyButtonPointerDown", applyButton.IsPressed);
                        if (!string.IsNullOrWhiteSpace(pressedScreenshotPath))
                        {
                            capture.Capture(activeWindow, pressedScreenshotPath);
                            Check("pressedScreenshotCaptured", File.Exists(Path.GetFullPath(pressedScreenshotPath)));
                        }
                        applyButton.RaiseEvent(new MouseButtonEventArgs(
                            Mouse.PrimaryDevice,
                            Environment.TickCount,
                            MouseButton.Left)
                        {
                            RoutedEvent = Mouse.MouseUpEvent
                        });
                        await activeWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                        await Task.Delay(20);
                        nativeInput.ReleasePointer();
                        interaction.MovePointerToCenter(waitCheckBox);
                        Mouse.Synchronize();
                        await activeWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                        await Task.Delay(100);
                        Check("applyButtonPointerReleased", !applyButton.IsPressed);
                        Check("applyButtonMouseLeaveRecovered", !applyButton.IsMouseOver && !applyButton.IsPressed);
                    }
                }

                await WaitForAsync(
                    () => !viewModel.Integration.IsBusy
                        && !viewModel.Integration.CanApplyResultToSimulation,
                    TimeSpan.FromSeconds(30),
                    "Machine explicit Result application did not complete.");
                Check("resultApplyDoesNotAdvanceTick",
                    viewModel.SceneSnapshots.Latest?.TickIndex == pendingTickIndex);
                var commandAppliedCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                Check("externalResultAppliedAtCommandBoundary",
                    commandAppliedCamera?.State == VirtualCameraState.FrameReady
                    && commandAppliedCamera.Result?.Decision == PlaceholderInspectionDecision.Pass
                    && commandAppliedCamera.ExternalResultEvidence?.TransactionId == id);

                if (!viewModel.StepCommand.CanExecute(null))
                {
                    throw new InvalidOperationException("Machine Step command was unavailable after Result application.");
                }

                viewModel.StepCommand.Execute(null);
                await WaitForAsync(
                    () => pendingTickIndex is { } pendingTick
                        && viewModel.SceneSnapshots.Latest?.TickIndex > pendingTick
                        && MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel)?.State
                            == VirtualCameraState.FrameReady,
                    TimeSpan.FromSeconds(30),
                    "Applied external Result was not visible on the next fixed Tick.");
                var appliedCamera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                resultVisibleTickIndex = viewModel.SceneSnapshots.Latest?.TickIndex;
                resultDocumentSha256 = appliedCamera?.ExternalResultEvidence?.ResultDocumentSha256;
                Check("externalResultPersistsOnNextTick",
                    resultVisibleTickIndex == pendingTickIndex + 1
                    && appliedCamera?.Result?.Decision == PlaceholderInspectionDecision.Pass
                    && appliedCamera.ExternalResultEvidence?.TransactionId == id
                    && appliedCamera.ExternalResultEvidence.ResultMessageId == resultBeforePull.MessageId);
                Check("externalResultExactDocumentHash",
                    string.Equals(
                        resultDocumentSha256,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                            Path.Combine(machineRoot, "transactions", id.ToString("D"), "result.json")))),
                        StringComparison.OrdinalIgnoreCase));
                Check("explicitApplyIsOneShot", !viewModel.Integration.CanApplyResultToSimulation);
                if (!string.IsNullOrWhiteSpace(appliedScreenshotPath))
                {
                    capture.Capture(activeWindow, appliedScreenshotPath);
                    Check("appliedScreenshotCaptured", File.Exists(Path.GetFullPath(appliedScreenshotPath)));
                }
            }
            status = viewModel.Integration.StatusText;
        }
        catch (Exception exception)
        {
            failures.Add(exception.GetBaseException().Message);
            status = exception.ToString();
        }
        finally
        {
            try
            {
                if (viewModel?.Integration.IsTcpListening == true)
                {
                    await viewModel.Integration.StopTcpListenerAsync();
                }
            }
            catch (Exception exception)
            {
                failures.Add("TCP listener cleanup: " + exception.GetBaseException().Message);
            }

            if (directTransport is not null)
            {
                await directTransport.DisposeAsync();
            }
            nativeInput?.Dispose();

            if (holdMilliseconds > 0)
            {
                await Task.Delay(holdMilliseconds);
            }

            if (window is not null && window.IsVisible)
            {
                window.Close();
            }

            SaveReport(
                reportTarget,
                mode,
                transactionId,
                status,
                applyInput,
                pendingTickIndex,
                resultVisibleTickIndex,
                resultDocumentSha256,
                pendingScreenshotPath,
                pressedScreenshotPath,
                appliedScreenshotPath,
                timeoutScreenshotPath,
                lateScreenshotPath,
                integrationProfilePath,
                automaticRunCompleted,
                automaticTimeoutObserved,
                lateResultQuarantined,
                lateResultDidNotMutateRuntime,
                monitor,
                checks,
                failures,
                cancellationScreenshotPath,
                automaticCancellationRequested,
                automaticCancellationAccepted,
                automaticCancelledResultObserved,
                duplicateCancellationWasRequested,
                duplicateCancellationAlreadyCancelled,
                cancellationDelayMilliseconds,
                cancellationBeforeAcknowledgementMode
                    ? "before-ack"
                    : cancellationBeforeRunMode
                        ? "before-run"
                    : cancelledThreeDMode
                        ? "after-restart-active-run"
                        : cancelAtTimeoutDeadlineMode
                            ? "same-wall-deadline"
                            : null,
                cancellationReceiptStatus);
        }

        return failures.Count == 0 && checks.Values.All(value => value) ? 0 : 1;
    }

    private static async Task<int> RunCrossModalOrderAsync(IReadOnlyList<string> args)
    {
        var mode = GetArgumentValue(args, ModeArgument) ?? "dual-order-3d-first";
        var threeDFirst = string.Equals(mode, "dual-order-3d-first", StringComparison.OrdinalIgnoreCase);
        var twoDFirst = string.Equals(mode, "dual-order-2d-first", StringComparison.OrdinalIgnoreCase);
        var concurrent = string.Equals(mode, "dual-concurrent", StringComparison.OrdinalIgnoreCase);
        if (!threeDFirst && !twoDFirst && !concurrent)
        {
            throw new ArgumentException($"Unsupported cross-modal order mode '{mode}'.");
        }
        var failureModality = GetArgumentValue(args, "--smoke-integration-failure-modality") ?? "none";
        var twoDExecutionErrorExpected = string.Equals(
            failureModality,
            "twoD-error",
            StringComparison.OrdinalIgnoreCase);
        var threeDExecutionErrorExpected = string.Equals(
            failureModality,
            "threeD-error",
            StringComparison.OrdinalIgnoreCase);
        var crossModalCancellationModality = GetArgumentValue(
            args,
            "--smoke-integration-cross-modal-cancel-modality") ?? "none";
        var twoDCancelledExpected = string.Equals(
            crossModalCancellationModality,
            "twoD",
            StringComparison.OrdinalIgnoreCase);
        var threeDCancelledExpected = string.Equals(
            crossModalCancellationModality,
            "threeD",
            StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(failureModality, "none", StringComparison.OrdinalIgnoreCase)
            && !twoDExecutionErrorExpected
            && !threeDExecutionErrorExpected)
        {
            throw new ArgumentException($"Unsupported cross-modal failure modality '{failureModality}'.");
        }
        if (!string.Equals(crossModalCancellationModality, "none", StringComparison.OrdinalIgnoreCase)
            && !twoDCancelledExpected
            && !threeDCancelledExpected)
        {
            throw new ArgumentException(
                $"Unsupported cross-modal cancellation modality '{crossModalCancellationModality}'.");
        }
        if ((twoDCancelledExpected || threeDCancelledExpected)
            && !concurrent)
        {
            throw new ArgumentException(
                "Cross-modal cancellation requires the dual-concurrent mode.");
        }
        if ((twoDCancelledExpected || threeDCancelledExpected)
            && !string.Equals(failureModality, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Cross-modal cancellation cannot be combined with an execution-error modality.");
        }
        var expectedTwoDImage = true;
        var expectedThreeDImage = !threeDExecutionErrorExpected && !threeDCancelledExpected;
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        var reportPath = GetArgumentValue(args, "--smoke-integration-exe-report")
            ?? Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-cross-modal-order-smoke.json");
        var reportTarget = Path.GetFullPath(reportPath);
        var integrationProfilePath = GetArgumentValue(args, "--smoke-integration-profile");
        var appliedScreenshotPath = GetArgumentValue(args, "--smoke-integration-applied-screenshot");
        var holdMilliseconds = ParseMilliseconds(
            GetArgumentValue(args, "--smoke-integration-exe-hold-ms"),
            0,
            0,
            30000);
        var crossModalCancellationDelayMilliseconds = ParseMilliseconds(
            GetArgumentValue(args, "--smoke-integration-cross-modal-cancel-delay-ms"),
            1000,
            0,
            30000);
        var transactionId = (Guid?)null;
        var status = string.Empty;
        MainViewModel? viewModel = null;
        ShellWindow? window = null;
        MachineIntegrationTcpExchange? directTransport = null;
        SmokeMonitorEvidence? monitor = null;
        SmokeWindowCapture? capture = null;

        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed && !failures.Contains(name, StringComparer.Ordinal))
            {
                failures.Add(name);
            }
        }

        try
        {
            var machineRoot = RequireArgument(args, "--smoke-integration-machine-root");
            var twoDRoot = RequireArgument(args, "--smoke-integration-consumer-root");
            var projectPath = RequireArgument(args, "--smoke-integration-project");
            var twoDRecipePath = RequireArgument(args, "--smoke-integration-recipe");
            var settingsPath = RequireArgument(args, "--smoke-integration-settings");
            var twoDSourcePath = RequireArgument(args, "--smoke-integration-two-d-source");
            var threeDSourcePath = RequireArgument(args, "--smoke-integration-source");
            var threeDRecipePath = RequireArgument(args, "--smoke-integration-three-d-recipe");
            var twoDVersion = RequireValue(args, "--smoke-integration-two-d-version");
            var twoDCommit = RequireValue(args, "--smoke-integration-two-d-commit");
            var threeDVersion = RequireValue(args, "--smoke-integration-three-d-version");
            var threeDCommit = RequireValue(args, "--smoke-integration-three-d-commit");
            var listenPort = ParsePort(GetArgumentValue(args, "--smoke-integration-listen-port"), 45101);
            var twoDPeerPort = ParsePort(GetArgumentValue(args, "--smoke-integration-two-d-peer-port"), 45102);
            var threeDPeerPort = ParsePort(GetArgumentValue(args, "--smoke-integration-three-d-peer-port"), 45203);
            var sharedKey = ReadSharedKey();
            Directory.CreateDirectory(machineRoot);
            Directory.CreateDirectory(twoDRoot);

            if (!string.IsNullOrWhiteSpace(integrationProfilePath))
            {
                var profilePath = Path.GetFullPath(integrationProfilePath);
                Check("integrationProfilePresent", File.Exists(profilePath));
                if (File.Exists(profilePath))
                {
                    try
                    {
                        using var profile = JsonDocument.Parse(File.ReadAllText(profilePath));
                        var profileRoot = profile.RootElement;
                        Check("integrationProfileSchema", profileRoot.GetProperty("schemaVersion").GetInt32() == 1);
                        Check(
                            "integrationProfileExchangeRoot",
                            string.Equals(
                                profileRoot.GetProperty("machineExchangeRoot").GetString(),
                                Path.GetFullPath(machineRoot),
                                StringComparison.OrdinalIgnoreCase));
                        Check(
                            "integrationProfileMachineListenPort",
                            profileRoot.GetProperty("machine").GetProperty("listenPort").GetInt32() == listenPort);
                    }
                    catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
                    {
                        Check("integrationProfileSchema", false);
                    }
                }
            }

            var project = new ProjectDocumentStore().Load(File.ReadAllText(projectPath));
            viewModel = new MainViewModel(project, projectPath, integrationSettingsPath: settingsPath);
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new ShellWindow { DataContext = viewModel };
            SmokeDpiTestHook.PlaceOnTestMonitor(window, 1280, 760);
            window.Show();
            monitor = SmokeDpiTestHook.CaptureMonitorEvidence(window);
            Check("machineWindowOnTestMonitor", monitor.WindowIntersectsMonitor);
            Check("machineWindowContainedOnTestMonitor", monitor.WindowContainedByMonitor);

            viewModel.IsRunMode = true;
            viewModel.Navigation.IsInspectionWorkspace = true;
            viewModel.Navigation.SelectedInspectionTabIndex = 1;
            Check("machineRunModeEnabled", viewModel.IsRunMode);
            ConfigureMachineIntegration(
                viewModel.Integration,
                machineRoot,
                twoDRecipePath,
                twoDVersion,
                twoDCommit,
                listenPort,
                twoDPeerPort,
                sharedKey,
                mode);
            Check("machineIntegrationConfigured", true);
            Check("machineSettingsCanBeSaved", viewModel.Integration.Setup.SaveSetupCommand.CanExecute(null));
            viewModel.Integration.Setup.SaveSetupCommand.Execute(null);
            await Task.Delay(100);
            Check("machineSettingsSaved", File.Exists(settingsPath));

            await viewModel.Integration.StartTcpListenerAsync();
            Check("machineListening", viewModel.Integration.IsTcpListening);
            directTransport = new MachineIntegrationTcpExchange(machineRoot, sharedKey);
            var threeDEndpoint = new TcpIntegrationEndpoint("127.0.0.1", threeDPeerPort);
            var twoDEndpoint = new TcpIntegrationEndpoint("127.0.0.1", twoDPeerPort);
            var producer = BuildIdentity.IntegrationIdentity;
            var threeDConsumer = CreateConsumerIdentity(
                IntegrationApplicationIds.ThreeDStudio,
                threeDVersion,
                threeDCommit);
            var twoDConsumer = CreateConsumerIdentity(
                IntegrationApplicationIds.TwoDStudio,
                twoDVersion,
                twoDCommit);

            async Task<Guid> RunThreeDAsync(bool first)
            {
                var pingAccepted = false;
                await WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(threeDEndpoint);
                            pingAccepted = receipt.PeerApplicationId == IntegrationApplicationIds.ThreeDStudio;
                            return pingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    first
                        ? "The 3D consumer did not accept a TCP Ping."
                        : "The 3D consumer did not accept a TCP Ping after the 2D Result.");
                Check("threeDConsumerPingAccepted", pingAccepted);

                var request = new MachineInspectionHandoffRequest(
                    project.Id,
                    "1.0",
                    "cross-modal-order-sequence",
                    "inspect-heightmap",
                    "camera-virtual",
                    "cross-modal-3d-acquisition",
                    "frame.c3d-grid-index",
                    "raw-height",
                    projectPath,
                    threeDSourcePath,
                    threeDRecipePath,
                    IntegrationInspectionModality.ThreeD,
                    IntegrationInspectionInputKind.HeightMap,
                    producer,
                    threeDConsumer);
                var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(machineRoot, request);
                if (first)
                {
                    transactionId = handoff.TransactionId;
                }

                Check(
                    "publishedThreeDHeightMapContract",
                    handoff.Context.Modality == IntegrationInspectionModality.ThreeD
                    && handoff.Context.InputKind == IntegrationInspectionInputKind.HeightMap
                    && handoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.ThreeDStudio);
                var pushReceipt = await directTransport.PushTransactionAsync(threeDEndpoint, handoff.TransactionId);
                Check(
                    "threeDTransactionPushed",
                    pushReceipt.Operation.Equals("push", StringComparison.OrdinalIgnoreCase)
                    && pushReceipt.PeerApplicationId == IntegrationApplicationIds.ThreeDStudio);
                await WaitForAsync(
                    () => HasCompletedResult(machineRoot, handoff.TransactionId),
                    TimeSpan.FromSeconds(120),
                    first
                        ? "Machine did not receive the completed 3D consumer Result before the 2D handoff."
                        : "Machine did not receive the completed 3D consumer Result after the 2D Result.");
                var result = MachineIntegrationExchange.ReadResult(machineRoot, handoff.TransactionId);
                if (threeDExecutionErrorExpected)
                {
                    Check(
                        "threeDConsumerResultExecutionError",
                        result.Status == IntegrationResultStatus.Failed
                        && result.Outcome == IntegrationInspectionOutcome.ExecutionError);
                }
                else
                {
                    Check(
                        "threeDConsumerResultCompletedPass",
                        result.Status == IntegrationResultStatus.Completed
                        && result.Outcome == IntegrationInspectionOutcome.Pass);
                }
                var pullReceipt = await directTransport.PullTransactionAsync(threeDEndpoint, handoff.TransactionId);
                Check(
                    "threeDResultPulledFromConsumer",
                    pullReceipt.Operation.Equals("pull", StringComparison.OrdinalIgnoreCase)
                    && pullReceipt.TransactionId == handoff.TransactionId);
                await RefreshIntegrationResultsAsync(
                    viewModel,
                    TimeSpan.FromSeconds(30),
                    "Machine Result refresh did not complete after the 3D Result.");
                if (first)
                {
                    if (threeDExecutionErrorExpected)
                    {
                        await WaitForAsync(
                            () => viewModel.Integration.LatestThreeDResultStatusText.Contains("ExecutionError", StringComparison.OrdinalIgnoreCase)
                                && viewModel.Integration.LatestThreeDResultStatusText.Contains("Failed", StringComparison.OrdinalIgnoreCase),
                            TimeSpan.FromSeconds(30),
                            "Machine did not project the 3D ExecutionError Result before the 2D handoff.");
                        Check("threeDExecutionErrorDisplayedBeforeTwoD", true);
                    }
                    else
                    {
                        await WaitForAsync(
                            () => viewModel.Integration.LatestThreeDResultStatusText.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                                && viewModel.Integration.LatestThreeDResultStatusText.Contains("Completed", StringComparison.OrdinalIgnoreCase),
                            TimeSpan.FromSeconds(30),
                            "Machine did not project the completed 3D Result status before the 2D handoff.");
                        Check("threeDResultDisplayedBeforeTwoD", true);
                    }

                    await WaitForAsync(
                        () => viewModel.Integration.HasLatestThreeDImage == expectedThreeDImage,
                        TimeSpan.FromSeconds(30),
                        "Machine did not project the expected 3D ResultEvidence image state before the 2D handoff.");
                    Check(
                        threeDExecutionErrorExpected
                            ? "threeDImageAbsentForExecutionError"
                            : "threeDImageAvailableBeforeTwoD",
                        viewModel.Integration.HasLatestThreeDImage == expectedThreeDImage);
                }

                return handoff.TransactionId;
            }

            async Task<Guid> RunTwoDAsync(bool first)
            {
                var pingAccepted = false;
                await WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(twoDEndpoint);
                            pingAccepted = receipt.PeerApplicationId == IntegrationApplicationIds.TwoDStudio;
                            return pingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    first
                        ? "The 2D consumer did not accept a TCP Ping."
                        : "The 2D consumer did not accept a TCP Ping after the 3D Result.");
                Check("twoDConsumerPingAccepted", pingAccepted);

                var request = new MachineInspectionHandoffRequest(
                    project.Id,
                    "1.0",
                    "cross-modal-order-sequence",
                    "inspect-image",
                    "camera-virtual",
                    "cross-modal-2d-acquisition",
                    "frame.cross-modal-2d",
                    "px",
                    projectPath,
                    twoDSourcePath,
                    twoDRecipePath,
                    IntegrationInspectionModality.TwoD,
                    IntegrationInspectionInputKind.Image,
                    producer,
                    twoDConsumer);
                var handoff = await MachineIntegrationHandoffPublisher.PublishAsync(machineRoot, request);
                if (first)
                {
                    transactionId = handoff.TransactionId;
                }

                Check(
                    "publishedTwoDImageContract",
                    handoff.Context.Modality == IntegrationInspectionModality.TwoD
                    && handoff.Context.InputKind == IntegrationInspectionInputKind.Image
                    && handoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.TwoDStudio);
                var pushReceipt = await directTransport.PushTransactionAsync(twoDEndpoint, handoff.TransactionId);
                Check(
                    "twoDTransactionPushed",
                    pushReceipt.Operation.Equals("push", StringComparison.OrdinalIgnoreCase)
                    && pushReceipt.PeerApplicationId == IntegrationApplicationIds.TwoDStudio);
                await WaitForAsync(
                    () => HasCompletedResult(machineRoot, handoff.TransactionId),
                    TimeSpan.FromSeconds(120),
                    first
                        ? "Machine did not receive the completed 2D consumer Result before the 3D handoff."
                        : "Machine did not receive the completed 2D consumer Result after the 3D Result.");
                var result = MachineIntegrationExchange.ReadResult(machineRoot, handoff.TransactionId);
                if (twoDExecutionErrorExpected)
                {
                    Check(
                        "twoDConsumerResultExecutionError",
                        result.Status == IntegrationResultStatus.Failed
                        && result.Outcome == IntegrationInspectionOutcome.ExecutionError);
                }
                else
                {
                    Check(
                        "twoDConsumerResultCompletedPass",
                        result.Status == IntegrationResultStatus.Completed
                        && result.Outcome == IntegrationInspectionOutcome.Pass);
                }
                var pullReceipt = await directTransport.PullTransactionAsync(twoDEndpoint, handoff.TransactionId);
                Check(
                    "twoDResultPulledFromConsumer",
                    pullReceipt.Operation.Equals("pull", StringComparison.OrdinalIgnoreCase)
                    && pullReceipt.TransactionId == handoff.TransactionId);
                await RefreshIntegrationResultsAsync(
                    viewModel,
                    TimeSpan.FromSeconds(30),
                    "Machine Result refresh did not complete after the 2D Result.");
                if (first)
                {
                    if (twoDExecutionErrorExpected)
                    {
                        await WaitForAsync(
                            () => viewModel.Integration.LatestTwoDResultStatusText.Contains("ExecutionError", StringComparison.OrdinalIgnoreCase)
                                && viewModel.Integration.LatestTwoDResultStatusText.Contains("Failed", StringComparison.OrdinalIgnoreCase),
                            TimeSpan.FromSeconds(30),
                            "Machine did not project the 2D ExecutionError Result before the 3D handoff.");
                        Check("twoDExecutionErrorDisplayedBeforeThreeD", true);
                    }
                    else
                    {
                        await WaitForAsync(
                            () => viewModel.Integration.LatestTwoDResultStatusText.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                                && viewModel.Integration.LatestTwoDResultStatusText.Contains("Completed", StringComparison.OrdinalIgnoreCase),
                            TimeSpan.FromSeconds(30),
                            "Machine did not project the completed 2D Result status before the 3D handoff.");
                        Check("twoDResultDisplayedBeforeThreeD", true);
                    }

                    await WaitForAsync(
                        () => viewModel.Integration.HasLatestTwoDImage == expectedTwoDImage,
                        TimeSpan.FromSeconds(30),
                        "Machine did not project the expected 2D ResultEvidence image state before the 3D handoff.");
                    Check(
                        twoDExecutionErrorExpected
                            ? "twoDSourceImageRetainedForExecutionError"
                            : "twoDImageAvailableBeforeThreeD",
                        viewModel.Integration.HasLatestTwoDImage == expectedTwoDImage);
                }

                return handoff.TransactionId;
            }

            async Task<(Guid ThreeDTransactionId, Guid TwoDTransactionId)> RunConcurrentAsync()
            {
                var threeDPingAccepted = false;
                var twoDPingAccepted = false;
                var threeDPingTask = WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(threeDEndpoint);
                            threeDPingAccepted = receipt.PeerApplicationId == IntegrationApplicationIds.ThreeDStudio;
                            return threeDPingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    "The 3D consumer did not accept a TCP Ping for concurrent cross-modal execution.");
                var twoDPingTask = WaitForAsync(
                    async () =>
                    {
                        try
                        {
                            var receipt = await directTransport.PingAsync(twoDEndpoint);
                            twoDPingAccepted = receipt.PeerApplicationId == IntegrationApplicationIds.TwoDStudio;
                            return twoDPingAccepted;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    "The 2D consumer did not accept a TCP Ping for concurrent cross-modal execution.");
                await Task.WhenAll(threeDPingTask, twoDPingTask);
                Check("threeDConsumerPingAccepted", threeDPingAccepted);
                Check("twoDConsumerPingAccepted", twoDPingAccepted);

                var threeDRequest = new MachineInspectionHandoffRequest(
                    project.Id,
                    "1.0",
                    "cross-modal-concurrent-sequence",
                    "inspect-heightmap",
                    "camera-virtual",
                    "cross-modal-3d-acquisition",
                    "frame.c3d-grid-index",
                    "raw-height",
                    projectPath,
                    threeDSourcePath,
                    threeDRecipePath,
                    IntegrationInspectionModality.ThreeD,
                    IntegrationInspectionInputKind.HeightMap,
                    producer,
                    threeDConsumer);
                var twoDRequest = new MachineInspectionHandoffRequest(
                    project.Id,
                    "1.0",
                    "cross-modal-concurrent-sequence",
                    "inspect-image",
                    "camera-virtual",
                    "cross-modal-2d-acquisition",
                    "frame.cross-modal-2d",
                    "px",
                    projectPath,
                    twoDSourcePath,
                    twoDRecipePath,
                    IntegrationInspectionModality.TwoD,
                    IntegrationInspectionInputKind.Image,
                    producer,
                    twoDConsumer);
                var handoffs = await Task.WhenAll(
                    MachineIntegrationHandoffPublisher.PublishAsync(machineRoot, threeDRequest),
                    MachineIntegrationHandoffPublisher.PublishAsync(machineRoot, twoDRequest));
                var threeDHandoff = handoffs[0];
                var twoDHandoff = handoffs[1];
                Check(
                    "publishedThreeDHeightMapContract",
                    threeDHandoff.Context.Modality == IntegrationInspectionModality.ThreeD
                    && threeDHandoff.Context.InputKind == IntegrationInspectionInputKind.HeightMap
                    && threeDHandoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.ThreeDStudio);
                Check(
                    "publishedTwoDImageContract",
                    twoDHandoff.Context.Modality == IntegrationInspectionModality.TwoD
                    && twoDHandoff.Context.InputKind == IntegrationInspectionInputKind.Image
                    && twoDHandoff.Context.ConsumerBuild.ApplicationId == IntegrationApplicationIds.TwoDStudio);

                var pushReceipts = await Task.WhenAll(
                    directTransport.PushTransactionAsync(threeDEndpoint, threeDHandoff.TransactionId),
                    directTransport.PushTransactionAsync(twoDEndpoint, twoDHandoff.TransactionId));
                Check(
                    "threeDTransactionPushed",
                    pushReceipts[0].Operation.Equals("push", StringComparison.OrdinalIgnoreCase)
                    && pushReceipts[0].PeerApplicationId == IntegrationApplicationIds.ThreeDStudio
                    && pushReceipts[0].TransactionId == threeDHandoff.TransactionId);
                Check(
                    "twoDTransactionPushed",
                    pushReceipts[1].Operation.Equals("push", StringComparison.OrdinalIgnoreCase)
                    && pushReceipts[1].PeerApplicationId == IntegrationApplicationIds.TwoDStudio
                    && pushReceipts[1].TransactionId == twoDHandoff.TransactionId);
                Check(
                    "crossModalConcurrentHandoffsPublished",
                    threeDHandoff.TransactionId != twoDHandoff.TransactionId);
                transactionId = threeDHandoff.TransactionId;

                if (twoDCancelledExpected || threeDCancelledExpected)
                {
                    var cancellationTransactionId = twoDCancelledExpected
                        ? twoDHandoff.TransactionId
                        : threeDHandoff.TransactionId;
                    var cancellationEndpoint = twoDCancelledExpected
                        ? twoDEndpoint
                        : threeDEndpoint;
                    Check(
                        "crossModalCancellationTargetPending",
                        !HasCompletedResult(machineRoot, cancellationTransactionId));
                    await Task.Delay(crossModalCancellationDelayMilliseconds);
                    var cancellationHandoff = directTransport.ReadHandoff(cancellationTransactionId);
                    var cancellationRequest = new IntegrationCancelRequestV2(
                        IntegrationContractSchema.V2,
                        IntegrationMessageKind.CancelRequest,
                        Guid.NewGuid(),
                        cancellationTransactionId,
                        cancellationHandoff.MessageId,
                        DateTimeOffset.UtcNow,
                        cancellationHandoff.Producer,
                        $"Machine Studio cross-modal {crossModalCancellationModality} cancellation");
                    var cancellationReceipt = await directTransport.CancelTransactionAsync(
                        cancellationEndpoint,
                        cancellationRequest);
                    Check("crossModalCancellationRequested", true);
                    Check(
                        "crossModalCancellationAccepted",
                        cancellationReceipt.Status == TcpIntegrationCancellationStatus.Accepted
                        && cancellationReceipt.TransactionId == cancellationTransactionId);

                    var duplicateHandoff = directTransport.ReadHandoff(cancellationTransactionId);
                    var duplicateRequest = new IntegrationCancelRequestV2(
                        IntegrationContractSchema.V2,
                        IntegrationMessageKind.CancelRequest,
                        Guid.NewGuid(),
                        cancellationTransactionId,
                        duplicateHandoff.MessageId,
                        DateTimeOffset.UtcNow,
                        duplicateHandoff.Producer,
                        $"Machine Studio cross-modal duplicate {crossModalCancellationModality} cancellation");
                    var duplicateReceipt = await directTransport.CancelTransactionAsync(
                        cancellationEndpoint,
                        duplicateRequest);
                    Check(
                        "crossModalDuplicateCancellationAlreadyCancelled",
                        duplicateReceipt.Status == TcpIntegrationCancellationStatus.AlreadyCancelled
                        && duplicateReceipt.TransactionId == cancellationTransactionId);
                }

                await WaitForAsync(
                    () => HasCompletedResult(machineRoot, threeDHandoff.TransactionId)
                        && HasCompletedResult(machineRoot, twoDHandoff.TransactionId),
                    TimeSpan.FromSeconds(120),
                    "Machine did not receive both concurrent cross-modal consumer Results.");
                var threeDResult = MachineIntegrationExchange.ReadResult(machineRoot, threeDHandoff.TransactionId);
                var twoDResult = MachineIntegrationExchange.ReadResult(machineRoot, twoDHandoff.TransactionId);
                Check(
                    "threeDConcurrentResultStatus",
                    threeDCancelledExpected
                        ? threeDResult.Status == IntegrationResultStatus.Cancelled
                            && threeDResult.Outcome == IntegrationInspectionOutcome.Indeterminate
                            && threeDResult.Error?.Code == IntegrationErrorCode.Cancelled
                        : threeDExecutionErrorExpected
                        ? threeDResult.Status == IntegrationResultStatus.Failed
                            && threeDResult.Outcome == IntegrationInspectionOutcome.ExecutionError
                        : threeDResult.Status == IntegrationResultStatus.Completed
                            && threeDResult.Outcome == IntegrationInspectionOutcome.Pass);
                Check(
                    "twoDConcurrentResultStatus",
                    twoDCancelledExpected
                        ? twoDResult.Status == IntegrationResultStatus.Cancelled
                            && twoDResult.Outcome == IntegrationInspectionOutcome.Indeterminate
                            && twoDResult.Error?.Code == IntegrationErrorCode.Cancelled
                        : twoDExecutionErrorExpected
                        ? twoDResult.Status == IntegrationResultStatus.Failed
                            && twoDResult.Outcome == IntegrationInspectionOutcome.ExecutionError
                        : twoDResult.Status == IntegrationResultStatus.Completed
                            && twoDResult.Outcome == IntegrationInspectionOutcome.Pass);

                var pullReceipts = await Task.WhenAll(
                    directTransport.PullTransactionAsync(threeDEndpoint, threeDHandoff.TransactionId),
                    directTransport.PullTransactionAsync(twoDEndpoint, twoDHandoff.TransactionId));
                Check(
                    "threeDResultPulledFromConsumer",
                    pullReceipts[0].Operation.Equals("pull", StringComparison.OrdinalIgnoreCase)
                    && pullReceipts[0].TransactionId == threeDHandoff.TransactionId);
                Check(
                    "twoDResultPulledFromConsumer",
                    pullReceipts[1].Operation.Equals("pull", StringComparison.OrdinalIgnoreCase)
                    && pullReceipts[1].TransactionId == twoDHandoff.TransactionId);
                await RefreshIntegrationResultsAsync(
                    viewModel,
                    TimeSpan.FromSeconds(30),
                    "Machine Result refresh did not complete after concurrent cross-modal Results.");
                return (threeDHandoff.TransactionId, twoDHandoff.TransactionId);
            }

            Guid threeDTransactionId;
            Guid twoDTransactionId;
            if (concurrent)
            {
                (threeDTransactionId, twoDTransactionId) = await RunConcurrentAsync();
            }
            else if (threeDFirst)
            {
                threeDTransactionId = await RunThreeDAsync(first: true);
                twoDTransactionId = await RunTwoDAsync(first: false);
            }
            else
            {
                twoDTransactionId = await RunTwoDAsync(first: true);
                threeDTransactionId = await RunThreeDAsync(first: false);
            }

            bool HasExpectedResultStatus(
                string statusText,
                bool executionErrorExpected,
                bool cancelledExpected) => cancelledExpected
                ? statusText.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
                    && statusText.Contains("Indeterminate", StringComparison.OrdinalIgnoreCase)
                : executionErrorExpected
                    ? statusText.Contains("ExecutionError", StringComparison.OrdinalIgnoreCase)
                        && statusText.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                    : statusText.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                        && statusText.Contains("Completed", StringComparison.OrdinalIgnoreCase);

            await WaitForAsync(
                () => HasExpectedResultStatus(
                        viewModel.Integration.LatestTwoDResultStatusText,
                        twoDExecutionErrorExpected,
                        twoDCancelledExpected)
                    && HasExpectedResultStatus(
                        viewModel.Integration.LatestThreeDResultStatusText,
                        threeDExecutionErrorExpected,
                        threeDCancelledExpected),
                TimeSpan.FromSeconds(30),
                "Machine did not retain the expected 2D and 3D Result statuses after the cross-modal order.");
            Check("crossModalBothResultsDisplayed", true);
            await WaitForAsync(
                () => viewModel.Integration.HasLatestTwoDImage == expectedTwoDImage
                    && viewModel.Integration.HasLatestThreeDImage == expectedThreeDImage,
                TimeSpan.FromSeconds(30),
                "Machine did not retain the expected modality image artifacts after the cross-modal order.");
            Check(
                "crossModalBothImagesAvailable",
                viewModel.Integration.HasLatestTwoDImage == expectedTwoDImage
                && viewModel.Integration.HasLatestThreeDImage == expectedThreeDImage
                && (!expectedTwoDImage
                    || viewModel.Integration.LatestTwoDImageUri is { IsFile: true } twoDImageUri
                        && File.Exists(twoDImageUri.LocalPath))
                && (!expectedThreeDImage
                    || viewModel.Integration.LatestThreeDImageUri is { IsFile: true } threeDImageUri
                        && File.Exists(threeDImageUri.LocalPath)));
            Check("crossModalDistinctTransactions", threeDTransactionId != twoDTransactionId);

            if (twoDExecutionErrorExpected)
            {
                Check(
                    "threeDResultPreservedAfterTwoDExecutionError",
                    HasExpectedResultStatus(viewModel.Integration.LatestThreeDResultStatusText, false, false));
                Check(
                    "threeDImagePreservedAfterTwoDExecutionError",
                    viewModel.Integration.HasLatestThreeDImage);
            }
            else if (threeDExecutionErrorExpected)
            {
                Check(
                    "twoDResultPreservedAfterThreeDExecutionError",
                    HasExpectedResultStatus(viewModel.Integration.LatestTwoDResultStatusText, false, false));
                Check(
                    "twoDImagePreservedAfterThreeDExecutionError",
                    viewModel.Integration.HasLatestTwoDImage);
            }
            else if (twoDCancelledExpected)
            {
                Check(
                    "threeDResultPreservedAfterTwoDCancelled",
                    HasExpectedResultStatus(viewModel.Integration.LatestThreeDResultStatusText, false, false));
                Check(
                    "twoDSourceImageRetainedAfterCancellation",
                    viewModel.Integration.HasLatestTwoDImage);
            }
            else if (threeDCancelledExpected)
            {
                Check(
                    "twoDResultPreservedAfterThreeDCancelled",
                    HasExpectedResultStatus(viewModel.Integration.LatestTwoDResultStatusText, false, false));
                Check(
                    "twoDImagePreservedAfterThreeDCancelled",
                    viewModel.Integration.HasLatestTwoDImage);
            }

            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            var mmi = SmokeVisualTreeQuery.FindVisualDescendant<MmiOperatorLayoutView>(window);
            var twoDImageViewer = mmi is null
                ? null
                : SmokeVisualTreeQuery.FindVisualDescendant<Image>(
                    mmi,
                    candidate => string.Equals(candidate.Name, "MmiTwoDImageViewer", StringComparison.Ordinal));
            var threeDImageViewer = mmi is null
                ? null
                : SmokeVisualTreeQuery.FindVisualDescendant<Image>(
                    mmi,
                    candidate => string.Equals(candidate.Name, "MmiThreeDImageViewer", StringComparison.Ordinal));
            var threeDImageStatus = mmi is null
                ? null
                : SmokeVisualTreeQuery.FindVisualDescendant<TextBlock>(
                    mmi,
                    candidate => string.Equals(
                        candidate.Text,
                        viewModel.Integration.LatestThreeDImageStatusText,
                        StringComparison.Ordinal));
            Check("mmiOperatorLayoutFoundAfterCrossModal", mmi is not null);
            if (expectedTwoDImage)
            {
                Check(
                    "twoDImageViewerRenderedAfterCrossModal",
                    twoDImageViewer is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Source: not null });
            }
            else
            {
                Check(
                    "twoDImageViewerAbsentForExecutionError",
                    twoDImageViewer is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Source: null });
            }

            if (expectedThreeDImage)
            {
                Check(
                    "threeDImageViewerRenderedAfterCrossModal",
                    threeDImageViewer is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0, Source: not null });
            }
            else
            {
                Check(
                    "threeDImageViewerAbsentForNonImageResult",
                    !viewModel.Integration.HasLatestThreeDImage
                    && threeDImageStatus is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 });
            }
            if (!string.IsNullOrWhiteSpace(appliedScreenshotPath))
            {
                threeDImageViewer?.BringIntoView();
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                capture = new SmokeWindowCapture();
                capture.Capture(window, appliedScreenshotPath);
                Check("appliedScreenshotCaptured", File.Exists(Path.GetFullPath(appliedScreenshotPath)));
            }

            status = viewModel.Integration.StatusText;
        }
        catch (Exception exception)
        {
            failures.Add(exception.GetBaseException().Message);
            status = exception.ToString();
        }
        finally
        {
            try
            {
                if (viewModel?.Integration.IsTcpListening == true)
                {
                    await viewModel.Integration.StopTcpListenerAsync();
                }
            }
            catch (Exception exception)
            {
                failures.Add("TCP listener cleanup: " + exception.GetBaseException().Message);
            }

            if (directTransport is not null)
            {
                await directTransport.DisposeAsync();
            }

            if (holdMilliseconds > 0)
            {
                await Task.Delay(holdMilliseconds);
            }

            if (window is not null && window.IsVisible)
            {
                window.Close();
            }

            SaveReport(
                reportTarget,
                mode,
                transactionId,
                status,
                "command",
                null,
                null,
                null,
                null,
                null,
                appliedScreenshotPath,
                null,
                null,
                integrationProfilePath,
                false,
                false,
                false,
                false,
                monitor,
                checks,
                failures);
        }

        return failures.Count == 0 && checks.Values.All(value => value) ? 0 : 1;
    }

    private static async Task RefreshIntegrationResultsAsync(
        MainViewModel viewModel,
        TimeSpan timeout,
        string failureMessage)
    {
        if (viewModel.Integration.RefreshResultsCommand.CanExecute(null))
        {
            viewModel.Integration.RefreshResultsCommand.Execute(null);
            await WaitForAsync(
                () => !viewModel.Integration.IsBusy,
                timeout,
                failureMessage);
        }
    }

    private static void ConfigureMachineIntegration(
        MachineIntegrationViewModel integration,
        string machineRoot,
        string recipePath,
        string consumerVersion,
        string consumerCommit,
        int listenPort,
        int peerPort,
        byte[] sharedKey,
        string mode,
        string? heightMapSourcePath = null,
        int heightMapWidth = 1280,
        int heightMapHeight = 840)
    {
        integration.Setup.ExchangeRoot = machineRoot;
        integration.Setup.InspectionRecipePath = recipePath;
        var automaticThreeDMode = string.Equals(mode, "automatic-3d-error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-3d-timeout-late", StringComparison.OrdinalIgnoreCase);
        if (automaticThreeDMode)
        {
            integration.Setup.UseThreeDHeightMap = true;
            integration.Setup.ThreeDHeightMapSourcePath = heightMapSourcePath ?? string.Empty;
            integration.Setup.ThreeDHeightMapWidthText = heightMapWidth.ToString();
            integration.Setup.ThreeDHeightMapHeightText = heightMapHeight.ToString();
            integration.Setup.ThreeDHeightMapPixelFormat = "Float32";
            integration.Setup.ThreeDHeightMapUnit = "raw-height";
            integration.Setup.ThreeDInspectionRecipePath = recipePath;
            integration.Setup.ThreeDConsumerVersion = consumerVersion;
            integration.Setup.ThreeDConsumerCommit = consumerCommit;
            integration.Setup.ThreeDSequenceId = "automatic-3d-inspection";
            integration.Setup.ThreeDStepId = "automatic-3d-trigger";
            integration.Setup.ThreeDDeviceId = "cam1";
        }
        integration.Setup.TwoDConsumerVersion =
            (string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d-delayed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d-error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dual-order-3d-first", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dual-order-2d-first", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dual-concurrent", StringComparison.OrdinalIgnoreCase))
                ? consumerVersion
                : string.Empty;
        integration.Setup.TwoDConsumerCommit =
            (string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d-delayed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "automatic-2d-error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dual-order-3d-first", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dual-order-2d-first", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "dual-concurrent", StringComparison.OrdinalIgnoreCase))
                ? consumerCommit
                : string.Empty;
        integration.Setup.WaitForExternalResult =
            string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-delayed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "automatic-2d-error", StringComparison.OrdinalIgnoreCase)
            || automaticThreeDMode
            || string.Equals(mode, "dual-order-3d-first", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "dual-order-2d-first", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "dual-concurrent", StringComparison.OrdinalIgnoreCase);
        integration.Setup.TcpListenAddress = "127.0.0.1";
        integration.Setup.TcpListenPortText = listenPort.ToString();
        integration.Setup.TcpPeerHost = "127.0.0.1";
        integration.Setup.TcpPeerPortText = peerPort.ToString();
        integration.SetSessionSharedKey(Convert.ToBase64String(sharedKey));
    }

    private static bool HasCompletedResult(string root, Guid transactionId)
    {
        try
        {
            var transaction = MachineIntegrationExchange.DiscoverTransactions(root)
                .SingleOrDefault(item => item.Handoff.TransactionId == transactionId);
            return transaction?.HasResult == true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or IntegrationContractException)
        {
            return false;
        }
    }

    private static IntegrationApplicationIdentity CreateConsumerIdentity(
        string applicationId,
        string version,
        string commit) =>
        new(
            applicationId,
            version,
            commit,
            IntegrationSourceState.Clean);

    private static byte[] ReadSharedKey()
    {
        using var store = new MachineIntegrationSharedKeyStore();
        var key = store.TryAcquire();
        if (key is not null)
        {
            return key;
        }

        var status = store.Status;
        throw new InvalidOperationException(status switch
        {
            MachineIntegrationSharedKeyStatus.Missing =>
                $"Environment variable {MachineIntegrationSharedKeyStore.EnvironmentVariableName} is required.",
            MachineIntegrationSharedKeyStatus.EnvironmentTooShort =>
                $"Environment variable {MachineIntegrationSharedKeyStore.EnvironmentVariableName} must contain at least 32 bytes.",
            _ =>
                $"Environment variable {MachineIntegrationSharedKeyStore.EnvironmentVariableName} must contain a Base64 key of at least 32 bytes."
        });
    }

    private static string DescribeAutomaticRecoveryState(
        SimulationSnapshot? snapshot,
        string sequenceId)
    {
        if (snapshot is null)
        {
            return "snapshot=<null>";
        }

        var sequence = snapshot.Sequences.FirstOrDefault(candidate =>
            string.Equals(candidate.SequenceId, sequenceId, StringComparison.Ordinal));
        var camera = snapshot.Cameras.FirstOrDefault();
        return $"runtimeGeneration={snapshot.RuntimeGeneration}; "
            + $"runMode={snapshot.RunMode}; "
            + $"automaticActive={snapshot.AutomaticRun.IsActive}; "
            + $"activeSequence={sequence?.ActiveSequenceId ?? "<null>"}; "
            + $"resetRetrySequenceId={snapshot.ResetRetrySequenceId ?? "<null>"}; "
            + $"sequenceStatus={sequence?.Status.ToString() ?? "<missing>"}; "
            + $"cameraState={camera?.State.ToString() ?? "<missing>"}";
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(failureMessage);
    }

    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(failureMessage);
    }

    private static void SaveReport(
        string path,
        string mode,
        Guid? transactionId,
        string status,
        string applyInput,
        long? pendingTickIndex,
        long? resultVisibleTickIndex,
        string? resultDocumentSha256,
        string? pendingScreenshotPath,
        string? pressedScreenshotPath,
        string? appliedScreenshotPath,
        string? timeoutScreenshotPath,
        string? lateScreenshotPath,
        string? integrationProfilePath,
        bool automaticRunCompleted,
        bool automaticTimeoutObserved,
        bool lateResultQuarantined,
        bool lateResultDidNotMutateRuntime,
        SmokeMonitorEvidence? monitor,
        IReadOnlyDictionary<string, bool> checks,
        IReadOnlyList<string> failures,
        string? cancellationScreenshotPath = null,
        bool automaticCancellationRequested = false,
        bool automaticCancellationAccepted = false,
        bool automaticCancelledResultObserved = false,
        bool duplicateCancellationRequested = false,
        bool duplicateCancellationAlreadyCancelled = false,
        int cancellationDelayMilliseconds = 0,
        string? cancellationOrder = null,
        string? cancellationReceiptStatus = null)
    {
        var report = new MachineIntegrationExeSmokeReport
        {
            Mode = mode,
            TransactionId = transactionId?.ToString("D"),
            Status = status,
            ApplyInput = applyInput,
            IntegrationProfilePath = integrationProfilePath,
            PendingTickIndex = pendingTickIndex,
            ResultVisibleTickIndex = resultVisibleTickIndex,
            ResultDocumentSha256 = resultDocumentSha256,
            PendingScreenshotPath = pendingScreenshotPath,
            PressedScreenshotPath = pressedScreenshotPath,
            AppliedScreenshotPath = appliedScreenshotPath,
            TimeoutScreenshotPath = timeoutScreenshotPath,
            LateScreenshotPath = lateScreenshotPath,
            CancellationScreenshotPath = cancellationScreenshotPath,
            AutomaticRunCompleted = automaticRunCompleted,
            AutomaticTimeoutObserved = automaticTimeoutObserved,
            AutomaticCancellationRequested = automaticCancellationRequested,
            AutomaticCancellationAccepted = automaticCancellationAccepted,
            AutomaticCancelledResultObserved = automaticCancelledResultObserved,
            DuplicateCancellationRequested = duplicateCancellationRequested,
            DuplicateCancellationAlreadyCancelled = duplicateCancellationAlreadyCancelled,
            CancellationDelayMilliseconds = cancellationDelayMilliseconds,
            CancellationOrder = cancellationOrder,
            CancellationReceiptStatus = cancellationReceiptStatus,
            LateResultQuarantined = lateResultQuarantined,
            LateResultDidNotMutateRuntime = lateResultDidNotMutateRuntime,
            Monitor = monitor,
            Checks = checks,
            Failures = failures
        };
        report.Save(path);
    }

    private static SmokeUiInteraction CreateInteraction(
        ShellWindow window,
        SmokeNativeInput nativeInput,
        SmokeWindowCapture capture) =>
        new()
        {
            FindTextBlock = (parent, predicate) =>
                SmokeVisualTreeQuery.FindVisualDescendant(parent, predicate),
            FindButton = (parent, predicate) =>
                SmokeVisualTreeQuery.FindVisualDescendant(parent, predicate),
            ActivateWindow = () => nativeInput.ActivateWindow(window),
            MovePointerToCenter = nativeInput.MovePointerToCenter,
            MouseEvent = nativeInput.SendMouseEvent,
            SetCursorPosition = nativeInput.SetCursorPosition,
            GetCursorPosition = nativeInput.GetCursorPosition,
            SetPopupContent = element => capture.SetPopupContent(element),
            MarkSmokePointerHeld = nativeInput.MarkPointerHeld,
            ReleaseSmokePointer = nativeInput.ReleasePointer,
            CheckPointerOwnership = nativeInput.CheckPointerOwnership
        };

    private static int ParsePort(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Machine integration TCP port must be between 1 and 65535.");
        }

        return port;
    }

    private static int ParseMilliseconds(string? value, int defaultValue, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var milliseconds)
            || milliseconds < minimum
            || milliseconds > maximum)
        {
            throw new ArgumentException(
                $"Machine integration hold milliseconds must be between {minimum} and {maximum}.");
        }

        return milliseconds;
    }

    private static int ParsePositiveInteger(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException("Machine integration HeightMap dimensions must be positive integers.");
        }

        return parsed;
    }

    private static string RequireArgument(IReadOnlyList<string> args, string name) =>
        GetArgumentValue(args, name) is { Length: > 0 } value
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing required argument '{name}'.");

    private static string RequireValue(IReadOnlyList<string> args, string name) =>
        GetArgumentValue(args, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"Missing required argument '{name}'.");

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
