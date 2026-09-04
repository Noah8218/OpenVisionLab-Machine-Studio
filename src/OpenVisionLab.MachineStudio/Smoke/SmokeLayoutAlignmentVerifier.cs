using System.IO;
using System.Text.Json;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeLayoutAlignmentReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyList<string> RequestedIds { get; init; }
    public required IReadOnlyList<string> SelectedIds { get; init; }
    public required string? PrimaryId { get; init; }
    public required string FinalAlignment { get; init; }
    public required IReadOnlyDictionary<string, double> MaximumDeviationByAlignment { get; init; }
    public required double MaximumNudgeDeviation { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public bool IsValid => Failures.Count == 0;

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeLayoutAlignmentVerifier
{
    internal static SmokeLayoutAlignmentReport Verify(
        MachineLayoutViewModel layout,
        IReadOnlyList<string> requestedIds,
        string finalAlignmentValue)
    {
        if (!Enum.TryParse(finalAlignmentValue, out LayoutSelectionAlignment finalAlignment))
        {
            throw new ArgumentException(
                $"Unsupported --smoke-layout-align '{finalAlignmentValue}'.");
        }

        var selected = layout.Items.Where(item => requestedIds.Contains(item.Id)).ToArray();
        var originalPositions = selected.ToDictionary(
            item => item.Id,
            item => (item.CurrentX, item.CurrentY),
            StringComparer.Ordinal);
        var deviations = new Dictionary<string, double>(StringComparer.Ordinal);
        var failures = new List<string>();

        if (selected.Length != requestedIds.Count)
        {
            failures.Add(
                $"Requested {requestedIds.Count} items but found {selected.Length}.");
        }

        foreach (var alignment in Enum.GetValues<LayoutSelectionAlignment>())
        {
            RestorePositions(selected, originalPositions);
            layout.SelectMany(requestedIds, requestedIds[^1]);
            layout.AlignSelection(alignment);

            var primary = layout.SelectedItem
                ?? throw new InvalidOperationException("Alignment reference item was not selected.");
            var anchor = GetAlignmentCoordinate(primary, alignment);
            var maximumDeviation = selected
                .Select(item => Math.Abs(GetAlignmentCoordinate(item, alignment) - anchor))
                .DefaultIfEmpty(double.PositiveInfinity)
                .Max();
            deviations[alignment.ToString()] = maximumDeviation;
            if (maximumDeviation > 0.000001d)
            {
                failures.Add($"{alignment} deviation was {maximumDeviation:R}.");
            }
        }

        RestorePositions(selected, originalPositions);
        layout.SelectMany(requestedIds, requestedIds[^1]);
        layout.AlignSelection(finalAlignment);

        var beforeNudge = selected.ToDictionary(
            item => item.Id,
            item => (item.CurrentX, item.CurrentY),
            StringComparer.Ordinal);
        var nudgeStep = layout.Definition?.SnapToGrid == false ? 1d : layout.GridSize;
        layout.NudgeSelection("Right");
        var maximumNudgeDeviation = selected
            .Select(item => Math.Max(
                Math.Abs((item.CurrentX - beforeNudge[item.Id].CurrentX) - nudgeStep),
                Math.Abs(item.CurrentY - beforeNudge[item.Id].CurrentY)))
            .DefaultIfEmpty(double.PositiveInfinity)
            .Max();
        if (maximumNudgeDeviation > 0.000001d)
        {
            failures.Add($"Group nudge deviation was {maximumNudgeDeviation:R}.");
        }
        layout.NudgeSelection("Left");
        layout.AlignSelection(finalAlignment);

        return new SmokeLayoutAlignmentReport
        {
            RequestedIds = requestedIds.ToArray(),
            SelectedIds = layout.SelectedItems.Select(item => item.Id).ToArray(),
            PrimaryId = layout.SelectedItem?.Id,
            FinalAlignment = finalAlignment.ToString(),
            MaximumDeviationByAlignment = deviations,
            MaximumNudgeDeviation = maximumNudgeDeviation,
            Failures = failures
        };
    }

    private static void RestorePositions(
        IEnumerable<LayoutItem> items,
        IReadOnlyDictionary<string, (double CurrentX, double CurrentY)> positions)
    {
        foreach (var item in items)
        {
            item.CurrentX = positions[item.Id].CurrentX;
            item.CurrentY = positions[item.Id].CurrentY;
        }
    }

    private static double GetAlignmentCoordinate(
        LayoutItem item,
        LayoutSelectionAlignment alignment)
    {
        var radians = item.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        var halfWidth = ((item.Width * cosine) + (item.Height * sine)) / 2d;
        var halfHeight = ((item.Width * sine) + (item.Height * cosine)) / 2d;
        return alignment switch
        {
            LayoutSelectionAlignment.Left => item.CurrentX - halfWidth,
            LayoutSelectionAlignment.HorizontalCenter => item.CurrentX,
            LayoutSelectionAlignment.Right => item.CurrentX + halfWidth,
            LayoutSelectionAlignment.Top => item.CurrentY - halfHeight,
            LayoutSelectionAlignment.VerticalCenter => item.CurrentY,
            LayoutSelectionAlignment.Bottom => item.CurrentY + halfHeight,
            _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, null)
        };
    }
}
