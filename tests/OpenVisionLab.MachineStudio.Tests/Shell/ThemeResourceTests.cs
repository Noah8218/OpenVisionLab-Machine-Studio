using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenVisionLab.MachineStudio.View.Inspector;
using OpenVisionLab.MachineStudio.View.Mmi;
using OpenVisionLab.MachineStudio.View.Project;
using OpenVisionLab.MachineStudio.View.Scene;
using OpenVisionLab.MachineStudio.View.Sequence;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.View.Simulation;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

[Collection(StudioUiTestCollection.Name)]
public sealed class ThemeResourceTests
{
    private readonly StudioUiTestHost _ui;

    public ThemeResourceTests(StudioUiTestHost ui) => _ui = ui;

    [Fact]
    public async Task R40LightPaletteReachesExistingViewResourceAliases()
    {
        await _ui.InvokeAsync(() =>
        {
            AssertColor(Application.Current, "Surface.Root", "#FFF3F5F8");
            AssertColor(Application.Current, "Surface.Panel", "#FFFFFFFF");
            AssertColor(Application.Current, "Accent.Primary", "#FF087F73");
            AssertColor(Application.Current, "CheckBoxCheckBackgroundFillChecked", "#FF087F73");
            AssertColor(Application.Current, "ComboBoxItemPillFillBrush", "#FF087F73");
            foreach (var view in new FrameworkElement[] { new GlobalCommandBarView(), new SequenceStepInspectorView() })
            {
                AssertColor(view, "Text.Primary", "#FF243448");
                AssertColor(view, "Text.Secondary", "#FF52687C");
            }
            AssertColor(new MmiOperatorLayoutView(), "Mmi.InspectionTabText", "#FF243448");
            AssertColor(new SceneDocumentView(), "EquipmentScene.Text", "#FF243448");
            AssertColor(new SimulationTestWorkspaceView(), "Scenario.InputSurface", "#FFFFFFFF");
            return true;
        });
    }

    [Fact]
    public async Task ApprovedDeviceAddKeepsItsDarkResourceClosureInsideTheLightApplication()
    {
        await _ui.InvokeAsync(() =>
        {
            var view = new EquipmentComponentLibraryDialogView();
            AssertColor(Application.Current, "Text.Primary", "#FF243448");
            AssertColor(view, "Text.Primary", "#FFE5EEF3");
            Assert.Equal("#FFFFFFFF", view.Foreground.ToString());
            AssertColor(view, "Accent.Primary", "#FF70D9C5");
            AssertColor(view, "Surface.Hover", "#FF254653");
            AssertColor(view, "Library.DialogSurface", "#FF122833");
            Assert.Same(view.FindResource("ApprovedDeviceAddTextBlockStyle"), ((Style)view.Resources[typeof(TextBlock)]).BasedOn);
            var button = new Button { Style = (Style)view.FindResource("GlobalCommandButtonStyle") };
            Assert.NotSame(Application.Current.FindResource("GlobalCommandButtonStyle"), button.Style);
            return true;
        });
    }

    private static void AssertColor(object owner, string key, string expected)
    {
        var value = owner is FrameworkElement view ? view.FindResource(key) : ((Application)owner).FindResource(key);
        var brush = Assert.IsType<SolidColorBrush>(value);
        Assert.Equal(expected, brush.Color.ToString());
        if (key == "Text.Primary") Assert.True(brush.CanFreeze);
    }
}
