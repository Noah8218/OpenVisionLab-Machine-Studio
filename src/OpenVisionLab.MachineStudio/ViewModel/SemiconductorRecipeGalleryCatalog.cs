using System.IO;
using OpenVisionLab.Machine.Core.Projects;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record SemiconductorRecipeGalleryItemDescriptor(
    string SourcePath,
    string FileName,
    string DisplayName,
    string ProjectSchema,
    string? SequenceName,
    IReadOnlyList<string> EquipmentFocus,
    string FallbackEquipment,
    int AxisCount,
    int SensorCount,
    int CylinderCount,
    int ConveyorCount,
    int WorkpieceCount,
    int DeviceCount,
    int ChannelCount,
    int ComponentCount,
    int StepCount);

internal sealed class SemiconductorRecipeGalleryCatalog
{
    private readonly ProjectDocumentStore _projectStore = new();

    internal IEnumerable<SemiconductorRecipeGalleryItemDescriptor> Enumerate(string galleryPath)
    {
        foreach (var sourcePath in Directory
                     .EnumerateFiles(galleryPath, "*.ovmachine")
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var project = _projectStore.Load(File.ReadAllText(sourcePath));
            var sequence = project.Sequences.FirstOrDefault();
            var sensorCount = project.Devices.Count(device => device.Sensor is not null);
            var cylinderCount = project.Devices.Count(device => device.Cylinder is not null);
            var conveyorCount = project.Devices.Count(device => device.Conveyor is not null);
            var workpieceCount = project.Devices.Count(device => device.Workpiece is not null);
            var equipmentFocus = project.Axes.Skip(1).Select(axis => axis.Name)
                .Concat(project.Devices
                    .Where(device => device.Id is not
                        ("device.transport" or
                         "device.sensor-entry" or
                         "device.sensor-process" or
                         "device.process-cylinder" or
                         "device.wafer"))
                    .Select(device => device.Name))
                .Distinct(StringComparer.CurrentCulture)
                .ToArray();

            yield return new SemiconductorRecipeGalleryItemDescriptor(
                sourcePath,
                Path.GetFileName(sourcePath),
                project.Name,
                project.Schema,
                sequence?.Name,
                equipmentFocus,
                project.Axes.Select(axis => axis.Name).FirstOrDefault() ?? string.Empty,
                project.Axes.Count,
                sensorCount,
                cylinderCount,
                conveyorCount,
                workpieceCount,
                project.Devices.Count,
                project.Channels.Count,
                project.Layouts.Sum(layout => layout.Components.Count),
                sequence?.Steps.Count ?? 0);
        }
    }
}
