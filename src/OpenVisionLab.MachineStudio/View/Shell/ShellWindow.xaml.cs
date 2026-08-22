using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace OpenVisionLab.MachineStudio.View.Shell;

public partial class ShellWindow : MachineFluentWindow
{
    private bool _closeApproved;
    private bool _closeResolutionRunning;

    public ShellWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAdaptiveLayout(ActualWidth);
        SizeChanged += (_, args) => UpdateAdaptiveLayout(args.NewSize.Width);
    }

    private void UpdateAdaptiveLayout(double width)
    {
        var compact = width < 1500;
        LeftWorkspaceColumn.Width = new GridLength(compact ? 220 : 280);
        RightWorkspaceColumn.Width = new GridLength(compact ? 300 : 360);

        if (DataContext is global::OpenVisionLab.MachineStudio.ViewModel.MainViewModel viewModel)
        {
            viewModel.IsCompactLayout = compact;
        }
    }

    internal ShellLayoutMetrics CaptureLayoutMetrics() =>
        new(
            ActualWidth,
            ActualHeight,
            ShellRoot.RowDefinitions[0].ActualHeight,
            ShellRoot.RowDefinitions[1].ActualHeight,
            ShellRoot.RowDefinitions[2].ActualHeight,
            ShellRoot.RowDefinitions[4].ActualHeight,
            LeftWorkspaceColumn.ActualWidth,
            CenterWorkspaceColumn.ActualWidth,
            RightWorkspaceColumn.ActualWidth,
            BottomWorkspaceRow.ActualHeight,
            WorkspaceColumnsGrid.ActualWidth,
            WorkspaceRowsGrid.ActualHeight);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled || IsTextEditingFocus() ||
            DataContext is not global::OpenVisionLab.MachineStudio.ViewModel.MainViewModel viewModel)
        {
            return;
        }

        var command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Delete, ModifierKeys.None) => viewModel.DeleteLayoutComponentCommand,
            (Key.D, ModifierKeys.Control) => viewModel.DuplicateLayoutSelectionCommand,
            _ => null
        };

        if (command?.CanExecute(null) != true)
        {
            return;
        }

        command.Execute(null);
        e.Handled = true;
    }

    private static bool IsTextEditingFocus() => Keyboard.FocusedElement is
        TextBoxBase or
        PasswordBox or
        ComboBox { IsEditable: true };

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeApproved || DataContext is not global::OpenVisionLab.MachineStudio.ViewModel.MainViewModel viewModel)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closeResolutionRunning)
        {
            return;
        }

        _closeResolutionRunning = true;
        try
        {
            if (await viewModel.TryResolveUnsavedChangesAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closeResolutionRunning = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
