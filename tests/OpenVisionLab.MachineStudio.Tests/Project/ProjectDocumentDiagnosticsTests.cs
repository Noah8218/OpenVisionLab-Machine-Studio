using System.Text;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.Projects;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectDocumentDiagnosticsTests
{
    [Fact]
    public void ReportsNoSavedPathWithoutChangingTheCurrentProject()
    {
        var project = new MachineProjectDocument { Name = "Untitled" };
        var report = new ProjectDocumentDiagnostics().Inspect(null, project, hasUnsavedChanges: false);

        Assert.True(report.IsHealthy);
        Assert.Null(report.ProjectPath);
        Assert.Contains(report.Items, item =>
            item.Code == ProjectDocumentDiagnosticCode.NoSavedPath);
        Assert.Equal("Untitled", project.Name);
    }

    [Fact]
    public async Task ReportsHealthyPrimaryAndCurrentUnsavedState()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "health.ovmachine");
            var project = new MachineProjectDocument { Name = "Healthy" };
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(project, path);

            var diagnostics = new ProjectDocumentDiagnostics();
            var healthy = diagnostics.Inspect(path, project, hasUnsavedChanges: false);
            Assert.True(healthy.IsHealthy);
            Assert.Contains(healthy.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.PrimaryFileHealthy);

            project.Name = "Edited";
            var dirty = diagnostics.Inspect(path, project, hasUnsavedChanges: true);
            Assert.Contains(dirty.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.CurrentDocumentHasUnsavedChanges);
            Assert.DoesNotContain(dirty.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.PrimaryFileDiffersFromCurrent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DoesNotFlagSupportedSchemaUpgradeAsAContentDifference()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "legacy-schema.ovmachine");
            var store = new ProjectDocumentStore();
            var legacy = new MachineProjectDocument
            {
                Schema = "1.5",
                Name = "Legacy"
            };
            File.WriteAllText(path, store.Serialize(legacy), Encoding.UTF8);

            var current = store.Load(File.ReadAllText(path, Encoding.UTF8));
            var report = new ProjectDocumentDiagnostics().Inspect(
                path,
                current,
                hasUnsavedChanges: false);

            Assert.DoesNotContain(report.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.PrimaryFileDiffersFromCurrent);
            Assert.Contains(report.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.PrimaryFileHealthy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsRecoveryWhenPrimaryFileIsMissingOrInvalid()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "recovery.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
            var diagnostics = new ProjectDocumentDiagnostics();
            var current = fileStore.Load(path);

            File.Delete(path);
            var missing = diagnostics.Inspect(path, current, hasUnsavedChanges: false);
            Assert.Contains(missing.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.PrimaryFileMissing);
            Assert.True(missing.CanPreviewRecovery);
            Assert.Equal(path + ".bak", missing.RecoverySourcePath);

            await File.WriteAllTextAsync(path, "not a machine project", Encoding.UTF8);
            var invalid = diagnostics.Inspect(path, current, hasUnsavedChanges: false);
            Assert.Contains(invalid.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.PrimaryFileInvalid);
            Assert.True(invalid.CanPreviewRecovery);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidBackupDoesNotProduceARecoveryCandidate()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "invalid-backup.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "First" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Second" }, path);
            await File.WriteAllTextAsync(path + ".bak", "broken backup", Encoding.UTF8);
            await File.WriteAllTextAsync(path, "broken primary", Encoding.UTF8);

            var report = new ProjectDocumentDiagnostics().Inspect(
                path,
                new MachineProjectDocument { Name = "Current" },
                hasUnsavedChanges: false);

            Assert.Contains(report.Items, item =>
                item.Code == ProjectDocumentDiagnosticCode.BackupFileInvalid);
            Assert.False(report.CanPreviewRecovery);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewAndRestoreUseTheBackupWithoutWritingDuringPreview()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "preview.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
            await File.WriteAllTextAsync(path, "corrupted primary", Encoding.UTF8);
            var primaryBefore = await File.ReadAllBytesAsync(path);
            var backupBefore = await File.ReadAllBytesAsync(path + ".bak");

            var diagnostics = new ProjectDocumentDiagnostics();
            var preview = diagnostics.PreviewRecovery(path);

            Assert.NotNull(preview);
            Assert.Equal("Backup", preview.ProjectName);
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
            Assert.Equal(backupBefore, await File.ReadAllBytesAsync(path + ".bak"));

            await fileStore.RestoreBackupAsync(preview!);
            Assert.Equal("Backup", fileStore.Load(path).Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewAcceptsUtf8BomBackups()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "bom-preview.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
            var backup = await File.ReadAllBytesAsync(path + ".bak");
            await File.WriteAllBytesAsync(
                path + ".bak",
                Encoding.UTF8.GetPreamble().Concat(backup).ToArray());
            await File.WriteAllTextAsync(path, "corrupted primary", Encoding.UTF8);

            var preview = new ProjectDocumentDiagnostics().PreviewRecovery(path);

            Assert.NotNull(preview);
            Assert.Equal("Backup", preview.ProjectName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsAStalePreviewAndLeavesPrimaryUntouched()
    {
        var directory = CreateTestDirectory();
        try
        {
            var path = Path.Combine(directory, "stale-preview.ovmachine");
            var fileStore = new ProjectDocumentFileStore();
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Backup" }, path);
            await fileStore.SaveAsync(new MachineProjectDocument { Name = "Current" }, path);
            await File.WriteAllTextAsync(path, "corrupted primary", Encoding.UTF8);
            var preview = new ProjectDocumentDiagnostics().PreviewRecovery(path);
            var primaryBefore = await File.ReadAllBytesAsync(path);
            await File.AppendAllTextAsync(path + ".bak", "changed", Encoding.UTF8);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fileStore.RestoreBackupAsync(preview!));
            Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(
            @"D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\project-document-diagnostics-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
