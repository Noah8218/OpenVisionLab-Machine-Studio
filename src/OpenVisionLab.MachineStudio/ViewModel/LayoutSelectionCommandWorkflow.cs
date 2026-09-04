using System.Globalization;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns WPF-neutral layout selection commands. Control-coordinate acquisition,
/// drag/transform requests, and pointer lifecycle remain in the shell/View.
/// </summary>
internal sealed class LayoutSelectionCommandWorkflow
{
    private readonly MachineLayoutViewModel _layout;
    private readonly Action<string> _setStatus;

    internal LayoutSelectionCommandWorkflow(
        MachineLayoutViewModel layout,
        Action<string> setStatus)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
    }

    internal void Nudge(object? parameter)
    {
        if (parameter is string direction)
        {
            _layout.NudgeSelection(direction);
        }
    }

    internal void Align(object? parameter)
    {
        if (parameter is not string value
            || !Enum.TryParse(value, out LayoutSelectionAlignment alignment)
            || !_layout.AlignSelection(alignment))
        {
            return;
        }

        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Status.LayoutAligned",
                "선택 장비를 {0} 기준으로 정렬했습니다.",
                "Aligned selected components to {0}."),
            OpenVisionLanguageService.T($"Alignment.{alignment}")));
    }

    internal void ChangeLayerOrder(object? parameter)
    {
        if (parameter is not string value
            || !Enum.TryParse(value, out LayoutLayerOrder order)
            || !_layout.ChangeSelectionLayerOrder(order))
        {
            return;
        }

        _setStatus(string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T(
                "Status.LayerOrderChanged",
                "선택 장비의 레이어 순서를 {0}(으)로 변경했습니다.",
                "Changed selected equipment layer order to {0}."),
            OpenVisionLanguageService.T($"LayerOrder.{order}")));
    }
}
