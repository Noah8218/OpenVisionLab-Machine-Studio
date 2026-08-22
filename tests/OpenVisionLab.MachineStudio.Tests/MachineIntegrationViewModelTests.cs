using OpenVisionLab.Integration.Contracts;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.MachineStudio.ViewModel.Integration;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class MachineIntegrationViewModelTests
{
    [Fact]
    public void SetupRoundTripDoesNotExportUntilExplicitCommand()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.Context();
        var viewModel = fixture.CreateViewModel(() => context);
        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionSourcePath = fixture.SourcePath;

        viewModel.SaveSetupCommand.Execute(null);

        Assert.True(File.Exists(fixture.SettingsPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.ExchangeRoot, "transactions")));

        var restored = fixture.CreateViewModel(() => context);

        Assert.Equal(Path.GetFullPath(fixture.ExchangeRoot), restored.ExchangeRoot);
        Assert.Equal(Path.GetFullPath(fixture.SourcePath), restored.InspectionSourcePath);
        Assert.False(string.IsNullOrWhiteSpace(restored.StatusText));
        Assert.False(Directory.Exists(Path.Combine(fixture.ExchangeRoot, "transactions")));
    }

    [Fact]
    public void ExportAndRefreshAreSeparateExplicitActionsAndTransactionRestores()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.Context();
        var viewModel = fixture.CreateViewModel(() => context);
        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionSourcePath = fixture.SourcePath;
        viewModel.SaveSetupCommand.Execute(null);

        viewModel.ExportHandoffCommand.Execute(null);

        var transactionsRoot = Path.Combine(fixture.ExchangeRoot, "transactions");
        var transaction = Assert.Single(Directory.GetDirectories(transactionsRoot));
        Assert.True(File.Exists(Path.Combine(transaction, "handoff.json")));
        Assert.Contains(Path.GetFileName(transaction), viewModel.TransactionSummary, StringComparison.OrdinalIgnoreCase);

        var restored = fixture.CreateViewModel(() => context);
        Assert.Contains(Path.GetFileName(transaction), restored.TransactionSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetDirectories(transactionsRoot));

        restored.RefreshResultCommand.Execute(null);

        Assert.False(string.IsNullOrWhiteSpace(restored.StatusText));
        Assert.Single(Directory.GetDirectories(transactionsRoot));
    }

    [Fact]
    public void ResetClearsRestoredSetupWithoutIntegrationAction()
    {
        using var fixture = new IntegrationFixture();
        var context = fixture.Context();
        var viewModel = fixture.CreateViewModel(() => context);
        viewModel.ExchangeRoot = fixture.ExchangeRoot;
        viewModel.InspectionSourcePath = fixture.SourcePath;
        viewModel.SaveSetupCommand.Execute(null);

        viewModel.ResetSetupCommand.Execute(null);
        var restored = fixture.CreateViewModel(() => context);

        Assert.Empty(restored.ExchangeRoot);
        Assert.Empty(restored.InspectionSourcePath);
        Assert.False(Directory.Exists(Path.Combine(fixture.ExchangeRoot, "transactions")));
    }

    private sealed class IntegrationFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "OpenVisionLab-Machine-Integration-UI",
            Guid.NewGuid().ToString("N"));

        public IntegrationFixture()
        {
            Directory.CreateDirectory(root);
            ExchangeRoot = Path.Combine(root, "exchange");
            Directory.CreateDirectory(ExchangeRoot);
            ProjectPath = Path.Combine(root, "project.ovmachine");
            SourcePath = Path.Combine(root, "source.c3d");
            SettingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(ProjectPath, "{}");
            File.WriteAllBytes(SourcePath, [1, 2, 3, 4]);
        }

        public string ExchangeRoot { get; }
        public string ProjectPath { get; }
        public string SourcePath { get; }
        public string SettingsPath { get; }

        public MachineIntegrationProjectContext Context() => new(
            new MachineProjectDocument
            {
                Id = "project-1",
                Name = "Integration Project",
                Schema = MachineProjectDocument.CurrentSchema
            },
            ProjectPath,
            "sequence-1",
            "step-1",
            "camera-1",
            HasUnsavedChanges: false);

        public MachineIntegrationViewModel CreateViewModel(
            Func<MachineIntegrationProjectContext> contextProvider) => new(
            contextProvider,
            SettingsPath,
            () => new IntegrationApplicationIdentity(
                IntegrationApplicationIds.MachineStudio,
                "0.1.0",
                new string('1', 40),
                IntegrationSourceState.Clean));

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
