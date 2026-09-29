using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Project;

public sealed class EquipmentOutlineSelectionCheckBox : CheckBox
{
    protected override AutomationPeer OnCreateAutomationPeer()
        => new EquipmentOutlineSelectionCheckBoxAutomationPeer(this);

    internal void InvokeAutomationToggle()
    {
        if (IsEnabled)
        {
            OnClick();
        }
    }
}

internal sealed class EquipmentOutlineSelectionCheckBoxAutomationPeer : CheckBoxAutomationPeer, IToggleProvider
{
    private readonly EquipmentOutlineSelectionCheckBox _owner;

    public EquipmentOutlineSelectionCheckBoxAutomationPeer(EquipmentOutlineSelectionCheckBox owner)
        : base(owner)
    {
        _owner = owner;
    }

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);

    ToggleState IToggleProvider.ToggleState => _owner.IsChecked switch
    {
        true => ToggleState.On,
        false => ToggleState.Off,
        null => ToggleState.Indeterminate
    };

    void IToggleProvider.Toggle()
    {
        if (!_owner.IsEnabled)
        {
            throw new ElementNotEnabledException();
        }

        _owner.InvokeAutomationToggle();
    }
}
