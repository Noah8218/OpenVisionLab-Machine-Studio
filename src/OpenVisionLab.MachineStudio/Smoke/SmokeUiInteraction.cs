using System;
using System.Windows;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeUiInteraction
{
    public required Func<DependencyObject, Func<TextBlock, bool>, TextBlock?> FindTextBlock { get; init; }
    public required Func<DependencyObject, Func<Button, bool>, Button?> FindButton { get; init; }
    public required Action ActivateWindow { get; init; }
    public required Action<FrameworkElement> MovePointerToCenter { get; init; }
    public required Action<uint, uint, uint, uint, UIntPtr> MouseEvent { get; init; }
    public required Action<int, int> SetCursorPosition { get; init; }
    public required Func<(int X, int Y)> GetCursorPosition { get; init; }
    public required Action<FrameworkElement> SetPopupContent { get; init; }
    public required Action MarkSmokePointerHeld { get; init; }
    public required Action ReleaseSmokePointer { get; init; }
    public required Func<Window, (bool IsOwned, string Diagnostic)> CheckPointerOwnership { get; init; }
}
