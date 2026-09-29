using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;
using OpenVisionLab.MachineStudio.Behavior;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class PasswordBoxBehaviorTests
{
    [Fact]
    public async Task RoutesPasswordChangesAndClearsOnResetTokenWithoutReenteringCommand()
    {
        var result = await RunOnStaAsync(() =>
        {
            var passwordBox = new PasswordBox();
            var values = new List<object?>();
            var behavior = new PasswordBoxBehavior
            {
                PasswordChangedCommand = new RelayCommand(value => values.Add(value))
            };
            Interaction.GetBehaviors(passwordBox).Add(behavior);

            const string password = "c2Vzc2lvbi1rZXktc2VjcmV0";
            passwordBox.Password = password;
            var firstValue = values.Single();

            behavior.ResetToken = 1;

            return (firstValue, passwordBox.Password, values.Count);
        });

        Assert.Equal("c2Vzc2lvbi1rZXktc2VjcmV0", result.firstValue);
        Assert.Equal(string.Empty, result.Item2);
        Assert.Equal(1, result.Item3);
    }

    private static Task<(object? firstValue, string Password, int Count)> RunOnStaAsync(
        Func<(object? firstValue, string Password, int Count)> action)
    {
        var completion = new TaskCompletionSource<(object? firstValue, string Password, int Count)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
