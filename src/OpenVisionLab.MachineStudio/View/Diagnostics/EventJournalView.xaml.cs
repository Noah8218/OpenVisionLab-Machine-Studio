using System.Windows;
using System.Windows.Controls;

namespace OpenVisionLab.MachineStudio.View.Diagnostics;

public partial class EventJournalView : UserControl
{
    public static readonly DependencyProperty IsInlineProperty = DependencyProperty.Register(nameof(IsInline), typeof(bool), typeof(EventJournalView), new PropertyMetadata(false));

    public bool IsInline
    {
        get => (bool)GetValue(IsInlineProperty);
        set => SetValue(IsInlineProperty, value);
    }

    public EventJournalView()
    {
        InitializeComponent();
    }
}
