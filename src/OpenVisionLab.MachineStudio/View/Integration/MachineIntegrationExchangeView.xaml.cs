using System.Windows;
using System.Windows.Controls;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.View.Integration;

public partial class MachineIntegrationExchangeView
{
    public MachineIntegrationExchangeView()
    {
        InitializeComponent();
    }

    private void TcpSharedKeyPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox
            && DataContext is MainViewModel mainViewModel)
        {
            mainViewModel.IntegrationExchange.SetSessionSharedKey(passwordBox.Password);
        }
    }
}
