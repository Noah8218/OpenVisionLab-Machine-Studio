namespace OpenVisionLab.MachineStudio.Model;

public sealed class PropertyItem
{
    public string Name { get; }
    public string Value { get; }
    public string Category { get; }
    public bool IsEditable { get; }

    public PropertyItem(string name, string value, string category = "General", bool isEditable = false)
    {
        Name = name;
        Value = value;
        Category = category;
        IsEditable = isEditable;
    }
}
