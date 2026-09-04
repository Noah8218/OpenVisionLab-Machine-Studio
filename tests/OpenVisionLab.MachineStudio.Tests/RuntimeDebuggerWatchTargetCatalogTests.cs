using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.IO.Channels;
using OpenVisionLab.Machine.Sequence.Runtime;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Engine;
using OpenVisionLab.Machine.Simulation.Layout;
using OpenVisionLab.Machine.Simulation.Snapshots;
using OpenVisionLab.MachineStudio.ViewModel;
using Xunit;

namespace OpenVisionLab.MachineStudio.Tests;

public sealed class RuntimeDebuggerWatchTargetCatalogTests
{
    [Fact]
    public void Build_CombinesTargetsAndPreservesDeterministicKindNameAndIdOrder()
    {
        var snapshot = new SimulationSnapshot(
            TimeSpan.Zero,
            0,
            SimulationRunMode.Paused,
            SimulationControlOwner.EmbeddedSequence,
            1,
            [
                new AxisSnapshot("axis-z", "Z Axis", AxisState.Idle, 0, 0),
                new AxisSnapshot("axis-a", "A Axis", AxisState.Idle, 0, 0)
            ],
            1,
            [
                new DigitalSignalSnapshot("signal-b", "Signal B", ChannelKind.DigitalOutput, false),
                new DigitalSignalSnapshot("signal-a", "Signal A", ChannelKind.DigitalInput, true)
            ],
            [
                new SequenceExecutionSnapshot(
                    "sequence-z",
                    SequenceExecutionStatus.Running,
                    null,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    null,
                    TimeSpan.Zero),
                new SequenceExecutionSnapshot(
                    "sequence-a",
                    SequenceExecutionStatus.Running,
                    null,
                    0,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    0,
                    null,
                    TimeSpan.Zero)
            ],
            [],
            AutomaticRunSnapshot.NotConfigured,
            [
                new LayoutComponentSnapshot("equipment-z", "Z Equipment", LayoutComponentKind.MachineFrame, 0, 0, 0, 1, 1, null, null),
                new LayoutComponentSnapshot("equipment-a", "A Equipment", LayoutComponentKind.MachineFrame, 0, 0, 0, 1, 1, null, null)
            ]);

        var catalog = new RuntimeDebuggerWatchTargetCatalog();

        IReadOnlyList<RuntimeWatchTarget> targets = catalog.Build(
            snapshot,
            sequenceId => sequenceId == "sequence-z" ? "Sequence Z" : "Sequence A");

        Assert.Equal(
            new[]
            {
                "Sequence:sequence-a",
                "Sequence:sequence-z",
                "Axis:axis-a",
                "Axis:axis-z",
                "Signal:signal-a",
                "Signal:signal-b",
                "Equipment:equipment-a",
                "Equipment:equipment-z"
            },
            targets.Select(target => $"{target.Kind}:{target.Id}"));
        Assert.Equal(
            new[]
            {
                "Sequence A",
                "Sequence Z",
                "A Axis",
                "Z Axis",
                "Signal A",
                "Signal B",
                "A Equipment",
                "Z Equipment"
            },
            targets.Select(target => target.Name));
    }
}
