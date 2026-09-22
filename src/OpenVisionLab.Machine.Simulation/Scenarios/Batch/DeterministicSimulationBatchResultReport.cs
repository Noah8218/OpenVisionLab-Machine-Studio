using System.Globalization;
using System.IO;
using System.Text;

namespace OpenVisionLab.Machine.Simulation.Scenarios;

/// <summary>
/// Creates a stable, human-readable view of a validated deterministic batch.
/// The report contains only package data; it does not read project files or
/// inspect runtime state.
/// </summary>
public static class DeterministicSimulationBatchResultReport
{
    public static string CreateMarkdown(DeterministicSimulationBatchResultPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.HasValidEvidenceHash())
        {
            throw new InvalidOperationException(
                "Invalid or incomplete batch evidence cannot be rendered as a report.");
        }

        var firstRun = package.Runs[0].Result;
        var fixedStepMilliseconds = TimeSpan.FromTicks(firstRun.FixedStepTicks)
            .TotalMilliseconds
            .ToString("G", CultureInfo.InvariantCulture);
        var builder = new StringBuilder()
            .AppendLine("# OpenVisionLab Machine Studio Simulation Result")
            .AppendLine()
            .AppendLine("## Batch")
            .AppendLine($"- Schema version: `{package.SchemaVersion.ToString(CultureInfo.InvariantCulture)}`")
            .AppendLine($"- Batch ID: `{Inline(package.BatchId)}`")
            .AppendLine($"- Build identity: `{Inline(package.BuildIdentity)}`")
            .AppendLine($"- Requested runs: `{package.RequestedRuns.ToString(CultureInfo.InvariantCulture)}`")
            .AppendLine($"- Completed runs: `{package.CompletedRuns.ToString(CultureInfo.InvariantCulture)}`")
            .AppendLine($"- Complete: `{package.IsComplete.ToString().ToLowerInvariant()}`")
            .AppendLine($"- Outcome: **{Outcome(package.IsSuccess)}**")
            .AppendLine($"- Reference evidence hash: `{Inline(package.ReferenceEvidenceHash)}`")
            .AppendLine($"- Batch evidence hash: `{Inline(package.EvidenceHash)}`")
            .AppendLine()
            .AppendLine("## Result provenance")
            .AppendLine("- Result source: `Deterministic local simulation (no external inspection executed)`")
            .AppendLine("- Verification level: `Model/logic evidence only (not a real inspection)`")
            .AppendLine($"- Clock mode: `Fixed-step simulation · {fixedStepMilliseconds} ms/tick`")
            .AppendLine($"- Input/project SHA-256: `{Inline(firstRun.ProjectHash)}`")
            .AppendLine("- Model kind: `Deterministic scenario model (no inspection model)`")
            .AppendLine()
            .AppendLine("## Scenario")
            .AppendLine($"- Project: `{Inline(firstRun.ProjectId)}` — {Display(firstRun.ProjectName)}")
            .AppendLine($"- Scenario: `{Inline(firstRun.ScenarioId)}` — {Display(firstRun.ScenarioName)}")
            .AppendLine($"- Target: `{Inline(firstRun.TargetId)}`")
            .AppendLine($"- Fixed step ticks: `{firstRun.FixedStepTicks.ToString(CultureInfo.InvariantCulture)}`")
            .AppendLine($"- Seed: `{firstRun.Seed.ToString(CultureInfo.InvariantCulture)}`")
            .AppendLine($"- Planned ticks: `{firstRun.PlannedTicks.ToString(CultureInfo.InvariantCulture)}`")
            .AppendLine()
            .AppendLine("## Runs")
            .AppendLine("| Run | Result | Reference | Executed ticks | Evidence hash | Failure reason |")
            .AppendLine("| ---: | --- | --- | ---: | --- | --- |");

        foreach (var run in package.Runs)
        {
            builder.Append("| ")
                .Append(run.RunIndex.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(Outcome(run.Result.IsSuccess))
                .Append(" | ")
                .Append(run.ReferenceComparison.IsMatch ? "MATCH" : "MISMATCH")
                .Append(" | ")
                .Append(run.Result.ExecutedTicks.ToString(CultureInfo.InvariantCulture))
                .Append(" | `")
                .Append(Inline(run.Result.EvidenceHash))
                .Append("` | ")
                .Append(Display(run.Result.FailureReason))
                .AppendLine(" |");
        }

        builder.AppendLine()
            .AppendLine("## Evidence hashes")
            .AppendLine("| Name | SHA-256 |")
            .AppendLine("| --- | --- |")
            .AppendLine($"| Project | `{Inline(firstRun.ProjectHash)}` |")
            .AppendLine($"| Commands | `{Inline(firstRun.CommandHash)}` |")
            .AppendLine($"| Conditions | `{Inline(firstRun.ConditionHash)}` |")
            .AppendLine($"| Faults | `{Inline(firstRun.FaultHash)}` |")
            .AppendLine($"| Workpieces | `{Inline(firstRun.WorkpieceHash)}` |")
            .AppendLine($"| Signals | `{Inline(firstRun.SignalHash)}` |")
            .AppendLine($"| Snapshots | `{Inline(firstRun.SnapshotHash)}` |")
            .AppendLine($"| Events | `{Inline(firstRun.EventHash)}` |")
            .AppendLine($"| Assertion definitions | `{Inline(firstRun.AssertionDefinitionHash)}` |")
            .AppendLine($"| Assertion outcomes | `{Inline(firstRun.AssertionOutcomeHash)}` |")
            .AppendLine($"| Tick evidence | `{Inline(firstRun.TickEvidenceHash)}` |")
            .AppendLine()
            .AppendLine("## Assertion outcomes");

        var assertions = package.Runs
            .SelectMany(run => run.Result.AssertionOutcomes.Select(outcome => (run.RunIndex, Outcome: outcome)))
            .ToArray();
        if (assertions.Length == 0)
        {
            builder.AppendLine("No assertions were configured.");
        }
        else
        {
            builder.AppendLine("| Run | Assertion | Kind | Outcome | Target | Expected | Actual | Observed tick | Detail |")
                .AppendLine("| ---: | --- | --- | --- | --- | --- | --- | ---: | --- |");
            foreach (var assertion in assertions)
            {
                var outcome = assertion.Outcome;
                builder.Append("| ")
                    .Append(assertion.RunIndex.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ")
                    .Append(Inline(outcome.AssertionId))
                    .Append(" | ")
                    .Append(outcome.Kind)
                    .Append(" | ")
                    .Append(Outcome(outcome.IsPassed))
                    .Append(" | ")
                    .Append(Display(outcome.TargetId))
                    .Append(" | ")
                    .Append(Display(outcome.ExpectedValue))
                    .Append(" | ")
                    .Append(Display(outcome.ActualValue))
                    .Append(" | ")
                    .Append(outcome.ObservedTickIndex.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ")
                    .Append(Display(outcome.Detail))
                    .AppendLine(" |");
            }
        }

        builder.AppendLine()
            .AppendLine("## First mismatch");
        if (package.FirstMismatch is not { } mismatch)
        {
            builder.AppendLine("No mismatch recorded.");
        }
        else
        {
            builder.AppendLine($"- Run: `{mismatch.RunIndex.ToString(CultureInfo.InvariantCulture)}`")
                .AppendLine($"- Code: `{Inline(mismatch.Code)}`")
                .AppendLine($"- Detail: {Display(mismatch.Detail)}")
                .AppendLine($"- Evidence kind: `{Inline(mismatch.EvidenceKind)}`")
                .AppendLine($"- Target: `{Inline(mismatch.TargetId)}`")
                .AppendLine($"- Observed tick: `{mismatch.ObservedTickIndex.ToString(CultureInfo.InvariantCulture)}`")
                .AppendLine($"- Evidence hash: `{Inline(mismatch.EvidenceHash)}`");
        }

        return builder.ToString();
    }

    public static void SaveToMarkdown(
        DeterministicSimulationBatchResultPackage package,
        string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A report path is required.", nameof(path));
        }

        var markdown = CreateMarkdown(package);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicEvidenceFile.Write(
            fullPath,
            temporaryPath => File.WriteAllText(
                temporaryPath,
                markdown,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
    }

    private static string Outcome(bool passed) => passed ? "PASS" : "FAIL";

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(none)" : Inline(value);

    private static string Inline(string? value) =>
        (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
