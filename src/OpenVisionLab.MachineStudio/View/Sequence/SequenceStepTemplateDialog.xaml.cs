using System.Windows;
using System.Windows.Media;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.View.Sequence;

public partial class SequenceStepTemplateDialog : Window
{
    private SequenceEditorViewModel? _axisEditor;

    public SequenceStepTemplateDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_axisEditor is not null)
        {
            _axisEditor.AxisStepAdded -= OnAxisStepAdded;
            _axisEditor.CameraStepAdded -= OnCameraStepAdded;
            _axisEditor.SignalStepAdded -= OnSignalStepAdded;
            _axisEditor.SubsequenceStepAdded -= OnSubsequenceStepAdded;
            _axisEditor.UnitStepAdded -= OnUnitStepAdded;
            _axisEditor.TransferStepAdded -= OnTransferStepAdded;
            _axisEditor.WorkpieceStepAdded -= OnWorkpieceStepAdded;
        }
        _axisEditor = args.NewValue as SequenceEditorViewModel;
        if (_axisEditor is not null)
        {
            _axisEditor.AxisStepAdded += OnAxisStepAdded;
            _axisEditor.CameraStepAdded += OnCameraStepAdded;
            _axisEditor.SignalStepAdded += OnSignalStepAdded;
            _axisEditor.SubsequenceStepAdded += OnSubsequenceStepAdded;
            _axisEditor.UnitStepAdded += OnUnitStepAdded;
            _axisEditor.TransferStepAdded += OnTransferStepAdded;
            _axisEditor.WorkpieceStepAdded += OnWorkpieceStepAdded;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (_axisEditor is not null)
        {
            _axisEditor.AxisStepAdded -= OnAxisStepAdded;
            _axisEditor.CameraStepAdded -= OnCameraStepAdded;
            _axisEditor.SignalStepAdded -= OnSignalStepAdded;
            _axisEditor.SubsequenceStepAdded -= OnSubsequenceStepAdded;
            _axisEditor.UnitStepAdded -= OnUnitStepAdded;
            _axisEditor.TransferStepAdded -= OnTransferStepAdded;
            _axisEditor.WorkpieceStepAdded -= OnWorkpieceStepAdded;
        }
        _axisEditor = null;
        DataContextChanged -= OnDataContextChanged;
        Closed -= OnClosed;
        SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!IsLoaded || (!CameraStepAuthoring.IsExpanded && !SignalStepAuthoring.IsExpanded && !SubsequenceStepAuthoring.IsExpanded && !UnitStepAuthoring.IsExpanded && !TransferStepAuthoring.IsExpanded && !WorkpieceStepAuthoring.IsExpanded) || Owner is not { IsVisible: true } owner) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var ownerTop = owner.PointToScreen(new Point(0, 0)).Y / dpi.DpiScaleY;
        var ownerBottom = owner.PointToScreen(new Point(0, owner.ActualHeight)).Y / dpi.DpiScaleY;
        Top = Math.Clamp(Top, ownerTop + 20, Math.Max(ownerTop + 20, ownerBottom - ActualHeight - 20));
    }

    private void OnAxisStepAdded(object? sender, EventArgs args) => Close();
    private void OnCameraStepAdded(object? sender, EventArgs args) => Close();
    private void OnSignalStepAdded(object? sender, EventArgs args) => Close();
    private void OnSubsequenceStepAdded(object? sender, EventArgs args) => Close();
    private void OnUnitStepAdded(object? sender, EventArgs args) => Close();
    private void OnTransferStepAdded(object? sender, EventArgs args) => Close();
    private void OnWorkpieceStepAdded(object? sender, EventArgs args) => Close();

    private void CloseDialog(object sender, RoutedEventArgs args) => Close();
}
