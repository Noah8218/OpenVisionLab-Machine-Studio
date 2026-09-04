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
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Integration.Transport.Tcp;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.MachineStudio;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.Smoke;

internal sealed class MachineIntegrationExeSmokeReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Role { get; init; } = "producer";
    public string Mode { get; init; } = string.Empty;
    public string? TransactionId { get; init; }
    public string Status { get; init; } = string.Empty;
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
        var reportPath = GetArgumentValue(args, "--smoke-integration-exe-report")
            ?? Path.Combine(Path.GetTempPath(), "OpenVisionLab-Machine-integration-exe-smoke.json");
        var reportTarget = Path.GetFullPath(reportPath);
        var transactionId = (Guid?)null;
        var status = string.Empty;
        MainViewModel? viewModel = null;
        ShellWindow? window = null;
        MachineIntegrationTcpExchange? directTransport = null;
        SmokeMonitorEvidence? monitor = null;

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
            if (!string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mode, "3d", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Machine integration EXE smoke mode must be '2d' or '3d'.");
            }

            var machineRoot = RequireArgument(args, "--smoke-integration-machine-root");
            var consumerRoot = RequireArgument(args, "--smoke-integration-consumer-root");
            var projectPath = RequireArgument(args, "--smoke-integration-project");
            var recipePath = RequireArgument(args, "--smoke-integration-recipe");
            var settingsPath = RequireArgument(args, "--smoke-integration-settings");
            var consumerVersion = RequireValue(args, "--smoke-integration-consumer-version");
            var consumerCommit = RequireValue(args, "--smoke-integration-consumer-commit");
            var listenPort = ParsePort(
                GetArgumentValue(args, "--smoke-integration-listen-port"),
                45101);
            var peerPort = ParsePort(
                GetArgumentValue(args, "--smoke-integration-peer-port"),
                string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
                    ? 45102
                    : 45103);
            var sharedKey = ReadSharedKey();
            Directory.CreateDirectory(machineRoot);
            Directory.CreateDirectory(consumerRoot);

            var project = new ProjectDocumentStore().Load(File.ReadAllText(projectPath));
            viewModel = new MainViewModel(
                project,
                projectPath,
                integrationSettingsPath: settingsPath);
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
            ConfigureMachineIntegration(
                viewModel.Integration,
                machineRoot,
                recipePath,
                consumerVersion,
                consumerCommit,
                listenPort,
                peerPort,
                sharedKey,
                mode);
            Check("machineSettingsCanBeSaved", viewModel.Integration.SaveSetupCommand.CanExecute(null));
            viewModel.Integration.SaveSetupCommand.Execute(null);
            await Task.Delay(100);
            Check("machineSettingsSaved", File.Exists(settingsPath));

            await viewModel.Integration.StartTcpListenerAsync();
            Check("machineListening", viewModel.Integration.IsTcpListening);

            directTransport = new MachineIntegrationTcpExchange(machineRoot, sharedKey);
            var consumerEndpoint = new TcpIntegrationEndpoint("127.0.0.1", peerPort);
            var consumerPingAccepted = false;
            if (string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase))
            {
                await MachineIntegrationExeSmokeCameraScenario.PrepareAsync(viewModel);
                var camera = MachineIntegrationExeSmokeCameraScenario.GetCurrentCamera(viewModel);
                Check("cameraFrameReady", camera?.State == VirtualCameraState.FrameReady);
                Check("cameraFrameIdentityAvailable", !string.IsNullOrWhiteSpace(viewModel.CurrentCameraFrameHashText));
                Check("machineBuildExactCommit", BuildIdentity.IsExactCommit);
                Check(
                    "machineProjectPathAvailable",
                    viewModel.CurrentProjectPath is { Length: > 0 } currentProjectPath
                    && File.Exists(currentProjectPath));
                Check("cameraAcquisitionAvailable", camera?.CurrentAcquisitionId is { Length: > 0 });
                Check(
                    "cameraFrameEvidenceAvailable",
                    camera?.FrameEvidence is not null || camera?.Result?.FrameEvidence is not null);
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
                Check("integrationNotBusy", !viewModel.Integration.IsBusy);
                Check("consumerIdentityAvailable", viewModel.Integration.TwoDConsumerIdentity is not null);
                Check("exchangeRootAvailable", Directory.Exists(viewModel.Integration.ExchangeRoot.Trim()));
                Check("integrationRecipeAvailable", File.Exists(viewModel.Integration.InspectionRecipePath.Trim()));
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
                try
                {
                    _ = BuildIdentity.IntegrationIdentity;
                    Check("machineIntegrationIdentityQualified", true);
                }
                catch (IntegrationContractException)
                {
                    Check("machineIntegrationIdentityQualified", false);
                }
                Check("cameraWorkflowReadyForPublish", viewModel.Integration.CanPublishTwoDImageHandoff);

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
            await WaitForAsync(
                () => HasCompletedResult(machineRoot, id),
                TimeSpan.FromSeconds(120),
                "Machine did not receive a completed consumer Result.");
            var resultBeforePull = MachineIntegrationExchange.ReadResult(machineRoot, id);
            Check(
                "consumerResultCompletedPass",
                resultBeforePull.Status == IntegrationResultStatus.Completed
                && resultBeforePull.Outcome == IntegrationInspectionOutcome.Pass
                && !string.IsNullOrWhiteSpace(resultBeforePull.RunId));

            var pullReceipt = await directTransport.PullTransactionAsync(
                consumerEndpoint,
                id);
            Check(
                "resultPulledFromConsumer",
                pullReceipt.Operation.Equals("pull", StringComparison.OrdinalIgnoreCase)
                && pullReceipt.TransactionId == id);

            if (viewModel.Integration.RefreshResultsCommand.CanExecute(null))
            {
                viewModel.Integration.RefreshResultsCommand.Execute(null);
                await WaitForAsync(
                    () => !viewModel.Integration.IsBusy,
                    TimeSpan.FromSeconds(30),
                    "Machine Result refresh did not complete.");
            }
            Check(
                "machineResultDisplayed",
                viewModel.Integration.ResultStatusText.Contains("Pass", StringComparison.OrdinalIgnoreCase)
                && viewModel.Integration.ResultStatusText.Contains("Completed", StringComparison.OrdinalIgnoreCase));
            status = viewModel.Integration.StatusText;
        }
        catch (Exception exception)
        {
            failures.Add(exception.GetBaseException().Message);
            status = exception.GetBaseException().ToString();
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

            if (window is not null && window.IsVisible)
            {
                window.Close();
            }

            SaveReport(
                reportTarget,
                mode,
                transactionId,
                status,
                monitor,
                checks,
                failures);
        }

        return failures.Count == 0 && checks.Values.All(value => value) ? 0 : 1;
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
        string mode)
    {
        integration.ExchangeRoot = machineRoot;
        integration.InspectionRecipePath = recipePath;
        integration.TwoDConsumerVersion =
            string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
                ? consumerVersion
                : string.Empty;
        integration.TwoDConsumerCommit =
            string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase)
                ? consumerCommit
                : string.Empty;
        integration.TcpListenAddress = "127.0.0.1";
        integration.TcpListenPortText = listenPort.ToString();
        integration.TcpPeerHost = "127.0.0.1";
        integration.TcpPeerPortText = peerPort.ToString();
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
        SmokeMonitorEvidence? monitor,
        IReadOnlyDictionary<string, bool> checks,
        IReadOnlyList<string> failures)
    {
        var report = new MachineIntegrationExeSmokeReport
        {
            Mode = mode,
            TransactionId = transactionId?.ToString("D"),
            Status = status,
            Monitor = monitor,
            Checks = checks,
            Failures = failures
        };
        report.Save(path);
    }

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
