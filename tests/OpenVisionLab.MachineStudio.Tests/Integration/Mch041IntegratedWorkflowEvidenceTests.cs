using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Infrastructure.Integration;
using OpenVisionLab.Machine.Persistence.Projects;
using OpenVisionLab.Machine.Simulation.Camera;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Compilation;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.TestSupport;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests.Integration;

public sealed class Mch041IntegratedWorkflowEvidenceTests
{
    private static readonly TimeSpan FixedStep = TimeSpan.FromMilliseconds(5);
    private static readonly IntegrationApplicationIdentity Producer = new(
        IntegrationApplicationIds.MachineStudio,
        "mch-041-machine-test",
        new string('1', 40),
        IntegrationSourceState.Clean);
    private static readonly IntegrationApplicationIdentity Consumer = new(
        IntegrationApplicationIds.TwoDStudio,
        "mch-033-stub-0.1.0",
        new string('a', 40),
        IntegrationSourceState.Clean);

    [Fact]
    public async Task PersistedWaferOcrSample_ClosesExplicitExternalWorkflowAndRecovery()
    {
        string runRoot = Path.Combine(
            TestStorage.RootPath,
            "mch-041-integrated-workflow-20260916",
            Guid.NewGuid().ToString("N"));
        string sampleRoot = Path.Combine(runRoot, "sample");
        string exchangeRoot = Path.Combine(runRoot, "exchange");
        string processLogRoot = Path.Combine(runRoot, "process-logs");
        string stubBuildRoot = Path.Combine(runRoot, "stub-build");
        Directory.CreateDirectory(sampleRoot);
        Directory.CreateDirectory(exchangeRoot);
        Directory.CreateDirectory(processLogRoot);

        string repositoryRoot = FindRepositoryRoot();
        string sourceSamplePath = Path.Combine(
            repositoryRoot,
            "samples",
            "SemiconductorRecipes",
            "04-WaferOcrInspection.ovmachine");
        string fixtureRoot = Path.Combine(
            repositoryRoot,
            "samples",
            "VisionInspectionCell",
            "assets",
            "mch-029");
        string normalFixturePath = Path.Combine(fixtureRoot, "normal-mono8.pgm");
        string badFixturePath = Path.Combine(fixtureRoot, "bad-mono8.pgm");
        string projectPath = Path.Combine(sampleRoot, "wafer-ocr.ovmachine");
        string inputPath = Path.Combine(sampleRoot, "assets", "input.pgm");
        string truncatedFixturePath = Path.Combine(sampleRoot, "assets", "truncated-mono8.pgm");
        string recipePath = Path.Combine(sampleRoot, "inspection-recipe.json");
        string consumerIdentityPath = Path.Combine(sampleRoot, "consumer-identity.json");
        string evidencePath = Path.Combine(runRoot, "mch-041-evidence.json");

        File.Copy(sourceSamplePath, projectPath, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        File.Copy(normalFixturePath, inputPath, overwrite: true);
        byte[] normalBytes = File.ReadAllBytes(normalFixturePath);
        File.WriteAllBytes(
            truncatedFixturePath,
            normalBytes[..^4]);
        File.WriteAllText(
            recipePath,
            "{\"schema\":\"vision-pipeline/1.0\",\"steps\":[{\"id\":\"mch-041-bright-pixel-check\"}]}",
            new UTF8Encoding(false));
        File.WriteAllText(
            consumerIdentityPath,
            "{\"applicationId\":\"OpenVisionLab.2DStudio\",\"applicationVersion\":\"mch-033-stub-0.1.0\",\"sourceCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sourceState\":\"clean\"}",
            new UTF8Encoding(false));

        var documentStore = new ProjectDocumentStore();
        string originalSampleSha256 = Sha256(File.ReadAllBytes(sourceSamplePath));
        MachineProjectDocument project = documentStore.Load(File.ReadAllText(projectPath));
        DeviceDefinition cameraDefinition = Assert.Single(
            project.Devices,
            device => device.Id == "camera.ocr");
        cameraDefinition.Camera!.SingleImageSource = new VirtualSingleImageSourceDefinition
        {
            SourceRelativePath = "assets/input.pgm",
            Width = 4,
            Height = 3,
            PixelFormat = "Mono8"
        };
        var fileStore = new ProjectDocumentFileStore(documentStore);
        await fileStore.SaveAsync(project, projectPath);
        project = await fileStore.LoadAsync(projectPath);

        var layout = Assert.Single(project.Layouts);
        Assert.Contains(layout.Components, component => component.BehaviorBindingId == "axis.process");
        Assert.Contains(project.Devices, device => device.Id == "camera.ocr" && device.Kind == DeviceKind.Camera);
        Assert.Contains(project.Devices, device => device.Id == "device.inspection-handoff" && device.Kind == DeviceKind.Inspection);
        Assert.Equal(9, layout.Components.Count);

        var compiler = new MachineProjectRuntimeCompiler(FixedStep);
        MachineProjectRuntimeCompilationResult compilation = compiler.Compile(project);
        Assert.True(compilation.IsSuccess, ErrorSummary(compilation));
        Assert.NotNull(compilation.Configuration?.Layout);
        Assert.Equal(2, compilation.Configuration!.Axes.Count);
        Assert.Single(compilation.Configuration.Cameras, camera => camera.Id == "camera.ocr");

        RecipeDryRunResult dryRun = await new DeterministicRecipeDryRunRunner().RunAsync(
            project,
            "automatic-cycle",
            maximumTicks: 2_000);
        Assert.Equal(RecipeDryRunOutcome.Completed, dryRun.Outcome);

        ProcessRun buildStub = await RunProcessAsync(
            "build-inspection-stub",
            repositoryRoot,
            processLogRoot,
            "build",
            Path.Combine(repositoryRoot, "tools", "MachineIntegrationInspectionStub", "MachineIntegrationInspectionStub.csproj"),
            "-c",
            "Release",
            "--nologo",
            "/nodeReuse:false",
            "/p:ContinuousIntegrationBuild=true",
            "/p:TreatWarningsAsErrors=true",
            $"-p:BaseOutputPath={EnsureTrailingSeparator(stubBuildRoot)}");
        Assert.Equal(0, buildStub.ExitCode);
        string stubDll = Path.Combine(
            stubBuildRoot,
            "Release",
            "net8.0",
            "MachineIntegrationInspectionStub.dll");
        Assert.True(File.Exists(stubDll), stubDll);

        using var engine = new FixedStepSimulationEngine(new SimulationSettings
        {
            FixedStep = FixedStep,
            TimeScale = 0.000001,
            Seed = project.Simulation.Seed
        });
        await engine.StartAsync();
        try
        {
            SimulationCommandResult configured = await engine.EnqueueCommandAsync(
                new ConfigureRuntimeCommand(compilation.Configuration!, project.Id));
            Assert.True(configured.IsAccepted, configured.Detail);

            var cases = new List<object>();
            CaseEvidence normal = await RunExternalCaseAsync(
                "normal",
                normalFixturePath,
                expectedOutcome: IntegrationInspectionOutcome.Pass,
                project,
                projectPath,
                recipePath,
                inputPath,
                exchangeRoot,
                processLogRoot,
                stubDll,
                engine);
            cases.Add(normal);

            CaseEvidence bad = await RunExternalCaseAsync(
                "bad",
                badFixturePath,
                expectedOutcome: IntegrationInspectionOutcome.Ng,
                project,
                projectPath,
                recipePath,
                inputPath,
                exchangeRoot,
                processLogRoot,
                stubDll,
                engine);
            cases.Add(bad);

            CaseEvidence failure = await RunFailedExternalCaseAsync(
                truncatedFixturePath,
                project,
                projectPath,
                recipePath,
                inputPath,
                exchangeRoot,
                processLogRoot,
                stubDll,
                engine);
            cases.Add(failure);

            string beforeReopen = documentStore.SerializeForEvidence(project);
            await fileStore.SaveAsync(project, projectPath);
            MachineProjectDocument reopened = await fileStore.LoadAsync(projectPath);
            string afterReopen = documentStore.SerializeForEvidence(reopened);
            Assert.Equal(beforeReopen, afterReopen);
            Assert.True(compiler.Compile(reopened).IsSuccess);

            var package = new
            {
                schemaVersion = "mch-041/1",
                status = "complete",
                sample = new
                {
                    sourcePath = sourceSamplePath,
                    sourceSha256 = originalSampleSha256,
                    persistedCopyPath = projectPath,
                    persistedCopySha256 = Sha256(File.ReadAllBytes(projectPath)),
                    projectId = project.Id,
                    schema = project.Schema,
                    layoutId = layout.Id,
                    layoutComponentCount = layout.Components.Count,
                    axisCount = project.Axes.Count,
                    cameraId = "camera.ocr",
                    inspectionHandoffId = "device.inspection-handoff"
                },
                dryRun = new
                {
                    sequenceId = dryRun.SequenceId,
                    outcome = dryRun.Outcome.ToString(),
                    executedTicks = dryRun.ExecutedTicks
                },
                cases,
                saveReopen = new
                {
                    canonicalSha256 = Sha256(Encoding.UTF8.GetBytes(beforeReopen)),
                    reopenedCanonicalSha256 = Sha256(Encoding.UTF8.GetBytes(afterReopen)),
                    equal = beforeReopen == afterReopen,
                    reopenExecuted = false
                },
                boundaries = new[]
                {
                    "MCH-034 automatic Sequence request/wait/resume remains Blocked·Skip.",
                    "Automatic timeout/late-result policy and real hardware/customer executable remain unverified.",
                    "The explicit publish, ACK, external Run, refresh/read, comparison, Apply, replay, Reset, and save/reopen actions remain separate."
                }
            };
            File.WriteAllText(
                evidencePath,
                JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            Assert.True(File.Exists(evidencePath));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private static async Task<CaseEvidence> RunExternalCaseAsync(
        string caseId,
        string fixturePath,
        IntegrationInspectionOutcome expectedOutcome,
        MachineProjectDocument project,
        string projectPath,
        string recipePath,
        string inputPath,
        string exchangeRoot,
        string processLogRoot,
        string stubDll,
        FixedStepSimulationEngine engine)
    {
        File.Copy(fixturePath, inputPath, overwrite: true);
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        string inputSha256 = Sha256(inputBytes);
        await StartManualControlAsync(engine);
        SimulationRuntimeIdentity runtime = CurrentRuntime(engine);
        var frame = new VirtualCameraFrameEvidence(
            $"camera.ocr/frame/00000001",
            "assets/input.pgm",
            inputSha256,
            inputBytes.LongLength,
            4,
            3,
            "Mono8");
        SimulationCommandResult trigger = await engine.EnqueueCommandAsync(
            new TriggerVirtualCameraCommand(
                "camera.ocr",
                "wafer-ocr-handoff",
                frame,
                projectId: project.Id,
                runtimeGeneration: runtime.RuntimeGeneration,
                waitForExternalResult: true));
        Assert.True(trigger.IsAccepted, trigger.Detail);
        await StepUntilAsync(engine, VirtualCameraState.AwaitingExternalResult);

        var context = new MachineIntegrationRequestContext(
            true,
            project.Id,
            project.Schema,
            project.Sequences,
            projectPath,
            "camera.ocr",
            "wafer-ocr-handoff",
            Assert.Single(engine.CurrentSnapshot.Cameras),
            new VirtualSingleImageSourceDefinition
            {
                SourceRelativePath = "assets/input.pgm",
                Width = 4,
                Height = 3,
                PixelFormat = "Mono8"
            });
        var request = new MachineIntegrationRequestWorkflow().TryCreate(
            context,
            recipePath,
            Producer,
            Consumer);
        Assert.NotNull(request);
        var workflow = new MachineIntegrationSimulationWorkflow(
            () => engine.CurrentSnapshot,
            command => engine.EnqueueCommandAsync(command));
        MachineIntegrationPublishRuntimeContext? publishContext = workflow.CapturePublishContext();
        IntegrationHandoffV2 handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            exchangeRoot,
            request!);
        Assert.True(workflow.TryRecordPublishedHandoff(handoff, publishContext));

        ProcessRun ack = await RunProcessAsync(
            $"ack-{caseId}",
            FindRepositoryRoot(),
            processLogRoot,
            stubDll,
            "--ack",
            exchangeRoot,
            handoff.TransactionId.ToString("D"),
            Path.Combine(Path.GetDirectoryName(recipePath)!, "consumer-identity.json"));
        Assert.Equal(0, ack.ExitCode);
        ProcessRun run = await RunProcessAsync(
            $"run-{caseId}",
            FindRepositoryRoot(),
            processLogRoot,
            stubDll,
            "--run",
            exchangeRoot,
            handoff.TransactionId.ToString("D"),
            Path.Combine(Path.GetDirectoryName(recipePath)!, "consumer-identity.json"),
            "--approve");
        Assert.Equal(0, run.ExitCode);

        MachineIntegrationValidatedResult validated = MachineIntegrationExchange.ReadValidatedResult(
            exchangeRoot,
            handoff.TransactionId);
        Assert.Equal(handoff.TransactionId, validated.Result.TransactionId);
        Assert.Equal(frame.FrameId, validated.Result.Correlation!.FrameId);
        Assert.Equal(inputSha256, validated.Handoff.Context.InputSha256, ignoreCase: true);
        Assert.Equal(expectedOutcome, validated.Result.Outcome);
        var observation = new MachineIntegrationResultObservationWorkflow(
            () => exchangeRoot,
            () => project.Id,
            () => true,
            () => false,
            () => Task.CompletedTask,
            action => action(),
            _ => { });
        observation.RecordPublishedHandoff(handoff);
        Assert.True((await observation.RefreshAsync()) >= 1);
        Assert.Equal(validated.Result.MessageId, observation.LatestValidatedResult?.Result.MessageId);
        observation.Dispose();

        var comparisonInput = MachineIntegrationResultComparisonInput.FromResult(
            validated.Result,
            "mch-041-recipe-1");
        MachineIntegrationResultComparison comparison = MachineIntegrationResultComparator.Compare(
            comparisonInput,
            comparisonInput);
        Assert.Equal(MachineIntegrationResultComparisonStatus.Match, comparison.Status);
        Assert.True(workflow.CanApply(validated));
        SimulationCommandResult applied = await workflow.ApplyAsync(validated);
        Assert.True(applied.IsAccepted, applied.Detail);
        VirtualCameraSnapshot camera = Assert.Single(engine.CurrentSnapshot.Cameras);
        Assert.Equal(VirtualCameraState.FrameReady, camera.State);
        Assert.Equal(
            expectedOutcome == IntegrationInspectionOutcome.Pass
                ? PlaceholderInspectionDecision.Pass
                : PlaceholderInspectionDecision.Fail,
            camera.Result?.Decision);
        Assert.False(workflow.CanApply(validated));
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.Equal(VirtualCameraState.Idle, Assert.Single(engine.CurrentSnapshot.Cameras).State);

        return new(
            caseId,
            fixturePath,
            inputSha256,
            handoff.TransactionId,
            ack.ExitCode,
            run.ExitCode,
            validated.Result.Status.ToString(),
            validated.Result.Outcome.ToString(),
            camera.State.ToString(),
            comparison.Status.ToString(),
            ReplayCanApply: false);
    }

    private static async Task<CaseEvidence> RunFailedExternalCaseAsync(
        string truncatedFixturePath,
        MachineProjectDocument project,
        string projectPath,
        string recipePath,
        string inputPath,
        string exchangeRoot,
        string processLogRoot,
        string stubDll,
        FixedStepSimulationEngine engine)
    {
        File.Copy(truncatedFixturePath, inputPath, overwrite: true);
        byte[] inputBytes = File.ReadAllBytes(inputPath);
        string inputSha256 = Sha256(inputBytes);
        await StartManualControlAsync(engine);
        SimulationRuntimeIdentity runtime = CurrentRuntime(engine);
        var frame = new VirtualCameraFrameEvidence(
            "camera.ocr/frame/00000001",
            "assets/input.pgm",
            inputSha256,
            inputBytes.LongLength,
            4,
            3,
            "Mono8");
        Assert.True((await engine.EnqueueCommandAsync(new TriggerVirtualCameraCommand(
            "camera.ocr",
            "wafer-ocr-handoff",
            frame,
            projectId: project.Id,
            runtimeGeneration: runtime.RuntimeGeneration,
            waitForExternalResult: true))).IsAccepted);
        await StepUntilAsync(engine, VirtualCameraState.AwaitingExternalResult);

        var requestContext = new MachineIntegrationRequestContext(
            true,
            project.Id,
            project.Schema,
            project.Sequences,
            projectPath,
            "camera.ocr",
            "wafer-ocr-handoff",
            Assert.Single(engine.CurrentSnapshot.Cameras),
            new VirtualSingleImageSourceDefinition
            {
                SourceRelativePath = "assets/input.pgm",
                Width = 4,
                Height = 3,
                PixelFormat = "Mono8"
            });
        var request = new MachineIntegrationRequestWorkflow().TryCreate(
            requestContext,
            recipePath,
            Producer,
            Consumer);
        Assert.NotNull(request);
        var workflow = new MachineIntegrationSimulationWorkflow(
            () => engine.CurrentSnapshot,
            command => engine.EnqueueCommandAsync(command));
        MachineIntegrationPublishRuntimeContext? publishContext = workflow.CapturePublishContext();
        IntegrationHandoffV2 handoff = await MachineIntegrationHandoffPublisher.PublishAsync(
            exchangeRoot,
            request!);
        Assert.True(workflow.TryRecordPublishedHandoff(handoff, publishContext));

        string identityPath = Path.Combine(Path.GetDirectoryName(recipePath)!, "consumer-identity.json");
        ProcessRun ack = await RunProcessAsync(
            "ack-truncated",
            FindRepositoryRoot(),
            processLogRoot,
            stubDll,
            "--ack",
            exchangeRoot,
            handoff.TransactionId.ToString("D"),
            identityPath);
        Assert.Equal(0, ack.ExitCode);
        ProcessRun run = await RunProcessAsync(
            "run-truncated",
            FindRepositoryRoot(),
            processLogRoot,
            stubDll,
            "--run",
            exchangeRoot,
            handoff.TransactionId.ToString("D"),
            identityPath,
            "--approve");
        Assert.NotEqual(0, run.ExitCode);
        var transaction = Assert.Single(
            MachineIntegrationExchange.DiscoverTransactions(exchangeRoot),
            item => item.Handoff.TransactionId == handoff.TransactionId);
        Assert.True(transaction.HasAcknowledgement);
        Assert.False(transaction.HasResult);
        Assert.False(workflow.CanApply(null));
        Assert.True((await engine.EnqueueCommandAsync(new ResetCommand())).IsAccepted);
        Assert.True(workflow.RefreshRuntimeState());
        Assert.Equal(VirtualCameraState.Idle, Assert.Single(engine.CurrentSnapshot.Cameras).State);

        return new(
            "truncated-failure",
            truncatedFixturePath,
            inputSha256,
            handoff.TransactionId,
            ack.ExitCode,
            run.ExitCode,
            "no-result",
            "execution-error",
            VirtualCameraState.Idle.ToString(),
            MachineIntegrationResultComparisonStatus.ComparisonUnavailable.ToString(),
            ReplayCanApply: false);
    }

    private static async Task StartManualControlAsync(FixedStepSimulationEngine engine)
    {
        SimulationCommandResult started = await engine.EnqueueCommandAsync(new StartManualControlCommand());
        Assert.True(started.IsAccepted, started.Detail);
        SimulationCommandResult paused = await engine.EnqueueCommandAsync(new PauseCommand());
        Assert.True(paused.IsAccepted, paused.Detail);
    }

    private static async Task StepUntilAsync(
        FixedStepSimulationEngine engine,
        VirtualCameraState expected)
    {
        for (var index = 0; index < 32; index++)
        {
            if (Assert.Single(engine.CurrentSnapshot.Cameras).State == expected)
            {
                return;
            }

            SimulationCommandResult step = await engine.EnqueueCommandAsync(new StepCommand());
            Assert.True(step.IsAccepted, step.Detail);
        }

        Assert.Equal(expected, Assert.Single(engine.CurrentSnapshot.Cameras).State);
    }

    private static SimulationRuntimeIdentity CurrentRuntime(FixedStepSimulationEngine engine) =>
        new(
            engine.CurrentSnapshot.ProjectId!,
            engine.CurrentSnapshot.RuntimeGeneration);

    private static async Task<ProcessRun> RunProcessAsync(
        string name,
        string workingDirectory,
        string logRoot,
        params string[] arguments)
    {
        string safeName = new string(name.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        string stdoutPath = Path.Combine(logRoot, $"{safeName}.stdout.txt");
        string stderrPath = Path.Combine(logRoot, $"{safeName}.stderr.txt");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await File.WriteAllTextAsync(stdoutPath, await stdout, new UTF8Encoding(false));
        await File.WriteAllTextAsync(stderrPath, await stderr, new UTF8Encoding(false));
        return new(name, process.ExitCode, stdoutPath, stderrPath);
    }

    private static string FindRepositoryRoot()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("MACHINE_STUDIO_REPO_ROOT");
        foreach (string startPath in new[]
                 {
                     configuredRoot ?? string.Empty,
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory,
                     @"C:\Git\Machine\Dev\OpenVisionLab-Machine-Studio-Dev"
                 })
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            DirectoryInfo? directory = new(startPath);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("The Machine repository root could not be resolved.");
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string ErrorSummary(MachineProjectRuntimeCompilationResult compilation) =>
        string.Join(
            "; ",
            compilation.Errors.Select(error => $"{error.Code}: {error.Message}"));

    private sealed record ProcessRun(
        string Name,
        int ExitCode,
        string StdoutPath,
        string StderrPath);

    private sealed record CaseEvidence(
        string CaseId,
        string SourcePath,
        string InputSha256,
        Guid TransactionId,
        int AcknowledgementExitCode,
        int ExternalRunExitCode,
        string ResultStatus,
        string ResultOutcome,
        string CameraStateAfterRecovery,
        string ComparisonStatus,
        bool ReplayCanApply);
}
