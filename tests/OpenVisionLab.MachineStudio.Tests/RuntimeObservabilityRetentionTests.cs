using System.Collections.ObjectModel;
using System.Reflection;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeObservabilityRetentionTests
{
    [Fact]
    public void LogMessages_RetainLatestThousandAsReadOnlyCollection()
    {
        using var viewModel = new MainViewModel();
        var appendLog = typeof(MainViewModel).GetMethod(
            "AppendLog",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AppendLog was not available.");

        for (var index = 0; index < 1005; index++)
        {
            appendLog.Invoke(
                viewModel,
                [TimeSpan.FromMilliseconds(index), "System", $"Retention test {index:0000}"]);
        }

        Assert.IsType<ReadOnlyObservableCollection<string>>(viewModel.LogMessages);
        Assert.Equal(1000, viewModel.LogMessages.Count);
        Assert.DoesNotContain(
            viewModel.LogMessages,
            line => line.Contains("Retention test 0000", StringComparison.Ordinal));
        Assert.Contains(
            viewModel.LogMessages,
            line => line.Contains("Retention test 1004", StringComparison.Ordinal));
    }
}
