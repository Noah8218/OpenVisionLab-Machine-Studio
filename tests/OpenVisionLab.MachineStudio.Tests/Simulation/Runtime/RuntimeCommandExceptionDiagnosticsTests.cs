using System.Reflection;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeCommandExceptionDiagnosticsTests
{
    [Fact]
    public void HandleCommandExceptionRecordsStructuredRuntimeContext()
    {
        using var viewModel = new MainViewModel();
        var handleCommandException = typeof(MainViewModel).GetMethod(
            "HandleCommandException",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleCommandException was not available.");

        handleCommandException.Invoke(
            viewModel,
            [new InvalidOperationException("command failure")]);

        var diagnostic = Assert.Single(
            viewModel.OperationalDiagnostics,
            item => item.EventName == "CommandFailed");
        Assert.Equal("Command failed", viewModel.StatusMessage);
        Assert.Equal(typeof(InvalidOperationException).FullName, diagnostic.ExceptionType);
        Assert.Equal("command failure", diagnostic.ExceptionMessage);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.ProjectId));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.SessionId));
        Assert.True(diagnostic.RuntimeGeneration.HasValue);
        Assert.Equal("HandleCommandException", diagnostic.Operation);
        Assert.Contains("projectId=", diagnostic.ToDiagnosticLine(), StringComparison.Ordinal);
        Assert.Contains("sessionId=", diagnostic.ToDiagnosticLine(), StringComparison.Ordinal);
        Assert.Contains("runtimeGeneration=", diagnostic.ToDiagnosticLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void HandleCommandExceptionRetainsStackAndInnerExceptionDetails()
    {
        using var viewModel = new MainViewModel();
        var handleCommandException = typeof(MainViewModel).GetMethod(
            "HandleCommandException",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleCommandException was not available.");

        Exception commandException;
        try
        {
            try
            {
                throw new InvalidOperationException("inner failure");
            }
            catch (Exception inner)
            {
                throw new ApplicationException("command failure", inner);
            }
        }
        catch (Exception exception)
        {
            commandException = exception;
        }

        handleCommandException.Invoke(viewModel, [commandException]);

        var diagnostic = Assert.Single(
            viewModel.OperationalDiagnostics,
            item => item.EventName == "CommandFailed");
        Assert.NotNull(commandException.StackTrace);
        Assert.Contains("ApplicationException", diagnostic.ExceptionDetail, StringComparison.Ordinal);
        Assert.Contains("command failure", diagnostic.ExceptionDetail, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", diagnostic.ExceptionDetail, StringComparison.Ordinal);
        Assert.Contains("inner failure", diagnostic.ExceptionDetail, StringComparison.Ordinal);
        Assert.Contains("at ", diagnostic.ExceptionDetail, StringComparison.Ordinal);
        var diagnosticLine = diagnostic.ToDiagnosticLine();
        Assert.Contains("exceptionDetail=", diagnosticLine, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', diagnosticLine);
        Assert.DoesNotContain('\r', diagnosticLine);
    }

    [Fact]
    public void HandleCommandExceptionSkipsRecordingAfterSessionCloseRequest()
    {
        using var viewModel = new MainViewModel();
        var closeRequested = typeof(MainViewModel).GetField(
            "_sessionCloseRequested",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Session close state was not available.");
        closeRequested.SetValue(viewModel, true);

        var handleCommandException = typeof(MainViewModel).GetMethod(
            "HandleCommandException",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleCommandException was not available.");

        handleCommandException.Invoke(
            viewModel,
            [new InvalidOperationException("ignored failure")]);

        Assert.DoesNotContain(
            viewModel.OperationalDiagnostics,
            item => item.EventName == "CommandFailed");
    }
}
