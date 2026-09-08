using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class ProjectDocumentSessionTests
{
    [Fact]
    public void OwnsProjectIdentityAndDirtyEvidenceBaseline()
    {
        var project = new MachineProjectDocument { Name = "Sample" };
        var session = new ProjectDocumentSession(
            new ProjectDocumentStore(),
            project,
            "sample.ovmachine");

        Assert.Same(project, session.Project);
        Assert.Equal("sample", session.DisplayName);
        Assert.Equal(Path.GetFullPath("sample.ovmachine"), session.CurrentPath);
        Assert.False(session.HasUnsavedChanges);

        project.Name = "Changed";

        Assert.True(session.RefreshDirtyState());
        Assert.True(session.HasUnsavedChanges);
        Assert.True(session.AcceptAsSaved());
        Assert.False(session.HasUnsavedChanges);
    }

    [Fact]
    public void ReplacesProjectAndClearsFileIdentityWithoutChangingSchema()
    {
        var firstProject = new MachineProjectDocument { Name = "First" };
        var session = new ProjectDocumentSession(
            new ProjectDocumentStore(),
            firstProject,
            "first.ovmachine");
        var replacement = new MachineProjectDocument { Name = "Untitled" };

        session.ReplaceProject(replacement);
        session.SetCurrentPath(null);

        Assert.Same(replacement, session.Project);
        Assert.Equal("Untitled", session.DisplayName);
        Assert.Null(session.CurrentPath);
        Assert.Equal(MachineProjectDocument.CurrentSchema, replacement.Schema);
    }

    [Fact]
    public void AppliesOnlyTheReceiptForTheCurrentRevisionAndContent()
    {
        var project = new MachineProjectDocument { Name = "Sample" };
        var session = new ProjectDocumentSession(
            new ProjectDocumentStore(),
            project,
            "sample.ovmachine");
        var revision = session.Revision;
        var receipt = new ProjectDocumentSaveReceipt(
            session.SessionId,
            revision,
            "saved.ovmachine",
            ProjectDocumentSession.ComputeContentHash(session.SerializeForEvidence()));

        Assert.True(session.TryApplySave(receipt));
        Assert.Equal(Path.GetFullPath("saved.ovmachine"), session.CurrentPath);

        project.Name = "Changed";
        session.MarkChanged();
        Assert.True(session.RefreshDirtyState());
        Assert.False(session.TryApplySave(receipt));
        Assert.Equal(Path.GetFullPath("saved.ovmachine"), session.CurrentPath);
        Assert.True(session.HasUnsavedChanges);
    }

    [Fact]
    public void RejectsAReceiptWhenContentChangesWithoutARevisionNotification()
    {
        var project = new MachineProjectDocument { Name = "Sample" };
        var session = new ProjectDocumentSession(
            new ProjectDocumentStore(),
            project,
            "sample.ovmachine");
        var receipt = new ProjectDocumentSaveReceipt(
            session.SessionId,
            session.Revision,
            "saved.ovmachine",
            ProjectDocumentSession.ComputeContentHash(session.SerializeForEvidence()));

        project.Name = "Changed";

        Assert.False(session.TryApplySave(receipt));
        Assert.Equal(Path.GetFullPath("sample.ovmachine"), session.CurrentPath);
    }
}
