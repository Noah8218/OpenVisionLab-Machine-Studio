using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class EquipmentInspectorField : ViewModelBase
{
    private readonly Action _changed;
    private string _value;
    private string _errorText = string.Empty;
    public EquipmentInspectorField(string id, string caption, string value, Action changed, double minimum = -1000000, double maximum = 1000000, bool integer = false, bool text = false, bool fullWidth = false, IReadOnlyList<LayoutPropertyOption>? options = null, string help = "")
    {
        Id = id; Caption = caption; _value = Baseline = value; _changed = changed;
        Help = help; Minimum = minimum; Maximum = maximum; IsInteger = integer; IsNumeric = !text && options is null; FullWidth = fullWidth; Options = options;
    }
    public string Id { get; }
    public string Caption { get; }
    public string CaptionText => Caption.LastIndexOf(" (", StringComparison.Ordinal) is var index && index >= 0 ? Caption[..index] : Caption;
    public string UnitText => Caption.LastIndexOf(" (", StringComparison.Ordinal) is var index && index >= 0 ? Caption[index..] : string.Empty;
    public string Help { get; }
    public string Baseline { get; }
    public string Value { get => _value; set { if (SetProperty(ref _value, value ?? string.Empty)) { SetError(string.Empty); _changed(); } } }
    public string ErrorText => _errorText;
    public bool HasError => _errorText.Length > 0;
    internal void SetError(string message)
    {
        if (!SetProperty(ref _errorText, message, nameof(ErrorText))) return;
        OnPropertyChanged(nameof(HasError));
    }
    public double Minimum { get; }
    public double Maximum { get; }
    public bool IsInteger { get; }
    public bool IsNumeric { get; }
    public bool FullWidth { get; set; }
    public IReadOnlyList<LayoutPropertyOption>? Options { get; }
    public bool IsChoice => Options is not null;
}

public sealed record EquipmentInspectorFieldRow(EquipmentInspectorField First, EquipmentInspectorField? Second)
{
    public int FirstColumnSpan => First.FullWidth ? 3 : 1;
    public bool IsLast { get; init; }
}

public sealed record EquipmentInspectorDetail(string Caption, string Value, string WarningText = "");
