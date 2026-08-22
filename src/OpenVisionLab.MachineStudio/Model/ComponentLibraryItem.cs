using OpenVisionLab.Machine.Core.Layouts;

namespace OpenVisionLab.MachineStudio.Model;

public sealed record ComponentLibraryItem(
    LayoutComponentKind Kind,
    string Name,
    string Category,
    string Description);
