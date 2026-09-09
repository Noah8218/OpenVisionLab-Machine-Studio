using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class SemiconductorRecipeGalleryCatalogTests
{
    [Fact]
    public void EnumerateExtractsStableRecipeMetadataWithoutCreatingViewModels()
    {
        var directory = CreateTestDirectory();
        try
        {
            var project = new MachineProjectDocument { Name = "Gallery recipe" };
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis.transport", Name = "Transport" });
            project.Axes.Add(new VirtualAxisDefinition { Id = "axis.wafer", Name = "Wafer axis" });
            project.Devices.Add(new DeviceDefinition
            {
                Id = "device.sensor",
                Name = "Entry sensor",
                Sensor = new DigitalSensorDefinition()
            });
            project.Devices.Add(new DeviceDefinition
            {
                Id = "device.cylinder",
                Name = "Clamp cylinder",
                Cylinder = new PneumaticCylinderDefinition()
            });
            project.Devices.Add(new DeviceDefinition
            {
                Id = "device.conveyor",
                Name = "Wafer conveyor",
                Conveyor = new ConveyorDefinition()
            });
            project.Devices.Add(new DeviceDefinition
            {
                Id = "device.workpiece",
                Name = "Wafer",
                Workpiece = new WorkpieceDefinition()
            });
            project.Layouts.Add(new MachineLayoutDefinition
            {
                Components = { new LayoutComponentDefinition { Id = "component.one" } }
            });
            project.Sequences.Add(new SequenceDefinition
            {
                Id = "sequence.load",
                Name = "Load sequence",
                Steps = { new SequenceStepDefinition { Id = "step.one" } }
            });
            File.WriteAllText(
                Path.Combine(directory, "01-recipe.ovmachine"),
                new ProjectDocumentStore().Serialize(project));

            var descriptor = Assert.Single(
                new SemiconductorRecipeGalleryCatalog().Enumerate(directory));

            Assert.Equal("01-recipe.ovmachine", descriptor.FileName);
            Assert.Equal("Gallery recipe", descriptor.DisplayName);
            Assert.Equal(MachineProjectDocument.CurrentSchema, descriptor.ProjectSchema);
            Assert.Equal("Load sequence", descriptor.SequenceName);
            Assert.Equal(new[] { "Wafer axis", "Entry sensor", "Clamp cylinder", "Wafer conveyor", "Wafer" },
                descriptor.EquipmentFocus);
            Assert.Equal("Transport", descriptor.FallbackEquipment);
            Assert.Equal(2, descriptor.AxisCount);
            Assert.Equal(1, descriptor.SensorCount);
            Assert.Equal(1, descriptor.CylinderCount);
            Assert.Equal(1, descriptor.ConveyorCount);
            Assert.Equal(1, descriptor.WorkpieceCount);
            Assert.Equal(4, descriptor.DeviceCount);
            Assert.Equal(1, descriptor.ComponentCount);
            Assert.Equal(1, descriptor.StepCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\semiconductor-recipe-gallery-catalog-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
