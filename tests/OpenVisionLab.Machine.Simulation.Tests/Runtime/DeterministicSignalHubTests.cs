using OpenVisionLab.Machine.Core.Channels;
using OpenVisionLab.Machine.IO.Channels;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class DeterministicSignalHubTests
{
    [Fact]
    public void Create_ValidDigitalSignals_CapturesStableIdOrder()
    {
        var result = DeterministicSignalHub.Create(new[]
        {
            Signal("do.done", ChannelKind.DigitalOutput, 1),
            Signal("di.start", ChannelKind.DigitalInput, 0)
        });

        Assert.True(result.IsAccepted);
        var snapshot = result.Hub!.CaptureSnapshot();
        Assert.Equal(new[] { "di.start", "do.done" }, snapshot.Signals.Select(signal => signal.Id));
        Assert.False(snapshot.Signals[0].Value);
        Assert.True(snapshot.Signals[1].Value);
    }

    [Fact]
    public void CaptureSnapshot_ReusesRevisionAndInvalidatesOnStateChanges()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.start", ChannelKind.DigitalInput, 0),
            Signal("do.done", ChannelKind.DigitalOutput, 0)
        }).Hub!;

        SignalHubSnapshot initial = hub.CaptureSnapshot();
        SignalWriteResult noOp = hub.SetDigitalInput(
            "di.start",
            false,
            SignalWriteOwner.Manual);

        Assert.False(noOp.StateChanged);
        Assert.Same(initial, hub.CaptureSnapshot());

        hub.SetDigitalInput("di.start", true, SignalWriteOwner.Manual);
        SignalHubSnapshot inputChanged = hub.CaptureSnapshot();

        Assert.NotSame(initial, inputChanged);
        Assert.False(initial.Signals.Single(signal => signal.Id == "di.start").Value);
        Assert.True(inputChanged.Signals.Single(signal => signal.Id == "di.start").Value);
        Assert.Same(inputChanged, hub.CaptureSnapshot());

        hub.SetDigitalInputOverride("di.start", false);
        SignalHubSnapshot overrideChanged = hub.CaptureSnapshot();

        Assert.NotSame(inputChanged, overrideChanged);
        Assert.Null(inputChanged.Signals.Single(signal => signal.Id == "di.start").OverrideValue);
        Assert.Equal(false, overrideChanged.Signals.Single(signal => signal.Id == "di.start").OverrideValue);

        hub.SetDigitalOutput("do.done", true, SignalWriteOwner.EmbeddedSequence);
        SignalHubSnapshot outputChanged = hub.CaptureSnapshot();

        Assert.NotSame(overrideChanged, outputChanged);
        Assert.True(outputChanged.Signals.Single(signal => signal.Id == "do.done").Value);

        hub.Reset();
        SignalHubSnapshot reset = hub.CaptureSnapshot();

        Assert.NotSame(outputChanged, reset);
        Assert.All(reset.Signals, signal => Assert.False(signal.Value));
        Assert.False(hub.Reset().StateChanged);
        Assert.Same(reset, hub.CaptureSnapshot());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(2)]
    public void Create_NonDiscreteInitialValue_IsRejected(double initialValue)
    {
        var result = DeterministicSignalHub.Create(new[]
        {
            Signal("di.start", ChannelKind.DigitalInput, initialValue)
        });

        Assert.False(result.IsAccepted);
        Assert.Equal(SignalHubErrorCode.InvalidInitialValue, result.ErrorCode);
    }

    [Fact]
    public void Writes_EnforceOwnershipAndDoNotReviseNoOps()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.start", ChannelKind.DigitalInput, 0),
            Signal("do.active", ChannelKind.DigitalOutput, 0)
        }).Hub!;

        var rejectedInput = hub.SetDigitalInput("di.start", true, SignalWriteOwner.EmbeddedSequence);
        var acceptedInput = hub.SetDigitalInput("di.start", true, SignalWriteOwner.Manual);
        var noOpInput = hub.SetDigitalInput("di.start", true, SignalWriteOwner.Manual);
        var componentInput = hub.SetDigitalInput("di.start", false, SignalWriteOwner.SimulationComponent);
        var manualOutput = hub.SetDigitalOutput("do.active", true, SignalWriteOwner.Manual);
        var rejectedComponentOutput = hub.SetDigitalOutput("do.active", true, SignalWriteOwner.SimulationComponent);
        var acceptedOutput = hub.SetDigitalOutput("do.active", true, SignalWriteOwner.EmbeddedSequence);

        Assert.Equal(SignalHubErrorCode.WriteOwnerNotAllowed, rejectedInput.ErrorCode);
        Assert.True(acceptedInput.IsAccepted);
        Assert.True(acceptedInput.StateChanged);
        Assert.True(noOpInput.IsAccepted);
        Assert.False(noOpInput.StateChanged);
        Assert.Equal(acceptedInput.Revision, noOpInput.Revision);
        Assert.True(componentInput.IsAccepted);
        Assert.True(componentInput.StateChanged);
        Assert.True(manualOutput.IsAccepted);
        Assert.True(manualOutput.StateChanged);
        Assert.Equal(SignalHubErrorCode.WriteOwnerNotAllowed, rejectedComponentOutput.ErrorCode);
        Assert.True(acceptedOutput.IsAccepted);
        Assert.False(acceptedOutput.StateChanged);
        Assert.Equal(3, hub.CaptureSnapshot().Revision);
    }

    [Fact]
    public void Create_AnalogAndDuplicateSignal_UsesSeparateContracts()
    {
        var analog = DeterministicSignalHub.Create(new[]
        {
            Signal("ai.height", ChannelKind.AnalogInput, 12.5),
            Signal("ao.speed", ChannelKind.AnalogOutput, -2.25)
        });
        var duplicate = DeterministicSignalHub.Create(new[]
        {
            Signal("di.start", ChannelKind.DigitalInput, 0),
            Signal("di.start", ChannelKind.DigitalInput, 1)
        });

        Assert.True(analog.IsAccepted);
        Assert.Equal(
            new[] { "ai.height", "ao.speed" },
            analog.Hub!.CaptureSnapshot().AnalogSignals.Select(signal => signal.Id));
        Assert.Equal(
            12.5,
            analog.Hub.CaptureSnapshot().AnalogSignals.Single(signal => signal.Id == "ai.height").Value);
        Assert.Equal(SignalHubErrorCode.DuplicateChannelId, duplicate.ErrorCode);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Create_NonFiniteAnalogInitialValue_IsRejected(double initialValue)
    {
        var result = DeterministicSignalHub.Create(new[]
        {
            Signal("ai.height", ChannelKind.AnalogInput, initialValue)
        });

        Assert.False(result.IsAccepted);
        Assert.Equal(SignalHubErrorCode.InvalidAnalogValue, result.ErrorCode);
        Assert.Equal("ai.height", result.ChannelId);
    }

    [Fact]
    public void AnalogReadsAndWrites_EnforceKindOwnershipAndFiniteValues()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("ai.height", ChannelKind.AnalogInput, 1.25),
            Signal("ao.speed", ChannelKind.AnalogOutput, -2.5),
            Signal("di.guard", ChannelKind.DigitalInput, 0)
        }).Hub!;

        AnalogSignalReadResult read = hub.ReadAnalogSignal("ai.height");
        AnalogSignalReadResult wrongKind = hub.ReadAnalogSignal("di.guard");
        AnalogSignalWriteResult rejectedInputOwner = hub.SetAnalogInput(
            "ai.height",
            2.5,
            SignalWriteOwner.EmbeddedSequence);
        AnalogSignalWriteResult acceptedInput = hub.SetAnalogInput(
            "ai.height",
            2.5,
            SignalWriteOwner.Manual);
        AnalogSignalWriteResult noOpInput = hub.SetAnalogInput(
            "ai.height",
            2.5,
            SignalWriteOwner.SimulationComponent);
        AnalogSignalWriteResult invalidInput = hub.SetAnalogInput(
            "ai.height",
            double.NaN,
            SignalWriteOwner.Manual);
        AnalogSignalWriteResult rejectedOutputOwner = hub.SetAnalogOutput(
            "ao.speed",
            3.5,
            SignalWriteOwner.SimulationComponent);
        AnalogSignalWriteResult acceptedOutput = hub.SetAnalogOutput(
            "ao.speed",
            3.5,
            SignalWriteOwner.EmbeddedSequence);

        Assert.True(read.IsAccepted);
        Assert.Equal(1.25, read.Value);
        Assert.Equal(ChannelKind.AnalogInput, read.Kind);
        Assert.Equal(SignalHubErrorCode.ChannelKindMismatch, wrongKind.ErrorCode);
        Assert.Equal(SignalHubErrorCode.WriteOwnerNotAllowed, rejectedInputOwner.ErrorCode);
        Assert.True(acceptedInput.IsAccepted);
        Assert.True(acceptedInput.StateChanged);
        Assert.True(noOpInput.IsAccepted);
        Assert.False(noOpInput.StateChanged);
        Assert.Equal(SignalHubErrorCode.InvalidAnalogValue, invalidInput.ErrorCode);
        Assert.Equal(SignalHubErrorCode.WriteOwnerNotAllowed, rejectedOutputOwner.ErrorCode);
        Assert.True(acceptedOutput.IsAccepted);
        Assert.True(acceptedOutput.StateChanged);
        Assert.Equal(2, hub.CaptureSnapshot().Revision);
    }

    [Fact]
    public void AnalogSnapshotAndReset_UseTheSharedRevisionBoundary()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("ai.height", ChannelKind.AnalogInput, 1.25),
            Signal("ao.speed", ChannelKind.AnalogOutput, -2.5)
        }).Hub!;

        SignalHubSnapshot initial = hub.CaptureSnapshot();
        AnalogSignalWriteResult noOp = hub.SetAnalogInput(
            "ai.height",
            1.25,
            SignalWriteOwner.Manual);
        SignalHubSnapshot sameRevision = hub.CaptureSnapshot();

        hub.SetAnalogOutput("ao.speed", 3.5, SignalWriteOwner.Manual);
        SignalHubSnapshot changed = hub.CaptureSnapshot();
        SignalHubResetResult resetResult = hub.Reset();
        SignalHubSnapshot reset = hub.CaptureSnapshot();

        Assert.True(noOp.IsAccepted);
        Assert.False(noOp.StateChanged);
        Assert.Same(initial, sameRevision);
        Assert.NotSame(initial, changed);
        Assert.Equal(1, changed.Revision);
        Assert.Equal(1, resetResult.ChangedSignalCount);
        Assert.Equal(2, resetResult.Revision);
        Assert.Equal(1.25, reset.AnalogSignals.Single(signal => signal.Id == "ai.height").Value);
        Assert.Equal(-2.5, reset.AnalogSignals.Single(signal => signal.Id == "ao.speed").Value);
        Assert.Same(reset, hub.CaptureSnapshot());
    }

    [Fact]
    public void Create_InvalidInterlockReferences_AreRejected()
    {
        var missing = DeterministicSignalHub.Create(new[]
        {
            Signal("do.active", ChannelKind.DigitalOutput, 0, "di.missing")
        });
        var wrongKind = DeterministicSignalHub.Create(new[]
        {
            Signal("do.guard", ChannelKind.DigitalOutput, 0),
            Signal("do.active", ChannelKind.DigitalOutput, 0, "do.guard")
        });
        var inputWithInterlock = DeterministicSignalHub.Create(new[]
        {
            Signal("di.guard", ChannelKind.DigitalInput, 1, "di.other")
        });
        var duplicate = DeterministicSignalHub.Create(new[]
        {
            Signal("di.guard", ChannelKind.DigitalInput, 1),
            Signal("do.active", ChannelKind.DigitalOutput, 0, "di.guard", "di.guard")
        });
        var selfReference = DeterministicSignalHub.Create(new[]
        {
            Signal("do.active", ChannelKind.DigitalOutput, 0, "do.active")
        });
        var blank = DeterministicSignalHub.Create(new[]
        {
            Signal("do.active", ChannelKind.DigitalOutput, 0, " ")
        });

        Assert.Equal(SignalHubErrorCode.InterlockChannelNotFound, missing.ErrorCode);
        Assert.Equal("di.missing", missing.ChannelId);
        Assert.Equal(SignalHubErrorCode.InterlockChannelKindMismatch, wrongKind.ErrorCode);
        Assert.Equal("do.guard", wrongKind.ChannelId);
        Assert.Equal(SignalHubErrorCode.InvalidInterlockConfiguration, inputWithInterlock.ErrorCode);
        Assert.Equal(SignalHubErrorCode.InvalidInterlockConfiguration, duplicate.ErrorCode);
        Assert.Equal(SignalHubErrorCode.InvalidInterlockConfiguration, selfReference.ErrorCode);
        Assert.Equal(SignalHubErrorCode.InvalidInterlockConfiguration, blank.ErrorCode);
    }

    [Fact]
    public void Create_ActiveOutputWithUnsatisfiedInterlock_IsRejected()
    {
        var result = DeterministicSignalHub.Create(new[]
        {
            Signal("di.guard", ChannelKind.DigitalInput, 0),
            Signal("do.active", ChannelKind.DigitalOutput, 1, "di.guard")
        });

        Assert.Equal(SignalHubErrorCode.InterlockNotSatisfied, result.ErrorCode);
        Assert.Equal("do.active", result.ChannelId);
    }

    [Fact]
    public void DigitalOutputInterlock_BlocksActivationAndDropsActiveOutputWhenInputFalls()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.guard", ChannelKind.DigitalInput, 0),
            Signal("do.active", ChannelKind.DigitalOutput, 0, "di.guard")
        }).Hub!;

        SignalWriteResult blocked = hub.SetDigitalOutput(
            "do.active",
            true,
            SignalWriteOwner.EmbeddedSequence);
        Assert.Equal(SignalHubErrorCode.InterlockNotSatisfied, blocked.ErrorCode);
        Assert.Equal(0, blocked.Revision);
        Assert.False(hub.ReadDigitalSignal("do.active").Value);

        Assert.True(hub.SetDigitalInput("di.guard", true, SignalWriteOwner.Manual).IsAccepted);
        Assert.True(hub.SetDigitalOutput(
            "do.active",
            true,
            SignalWriteOwner.EmbeddedSequence).IsAccepted);
        long activeRevision = hub.CaptureSnapshot().Revision;

        SignalWriteResult guardDropped = hub.SetDigitalInput(
            "di.guard",
            false,
            SignalWriteOwner.Manual);
        Assert.True(guardDropped.IsAccepted);
        Assert.Equal(activeRevision + 1, guardDropped.Revision);
        Assert.False(hub.ReadDigitalSignal("do.active").Value);

        SignalWriteResult failSafeOff = hub.SetDigitalOutput(
            "do.active",
            false,
            SignalWriteOwner.EmbeddedSequence);
        Assert.True(failSafeOff.IsAccepted);
        Assert.False(failSafeOff.StateChanged);
    }

    [Fact]
    public void DigitalOutputPairInterlock_RejectsWithoutPartialMutationAndCommitsOneRevision()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.guard", ChannelKind.DigitalInput, 0),
            Signal("do.reverse", ChannelKind.DigitalOutput, 0),
            Signal("do.run", ChannelKind.DigitalOutput, 0, "di.guard")
        }).Hub!;

        SignalHubSnapshot beforeRejected = hub.CaptureSnapshot();
        DigitalOutputPairWriteResult rejected = hub.SetDigitalOutputPairAtomically(
            "do.reverse",
            true,
            "do.run",
            true,
            SignalWriteOwner.Manual);
        SignalHubSnapshot afterRejected = hub.CaptureSnapshot();

        Assert.False(rejected.IsAccepted);
        Assert.Equal(SignalHubErrorCode.InterlockNotSatisfied, rejected.ErrorCode);
        Assert.Equal("do.run", rejected.ChannelId);
        Assert.Equal(beforeRejected.Revision, rejected.Revision);
        Assert.Equal(beforeRejected.Revision, afterRejected.Revision);
        Assert.False(afterRejected.Signals.Single(signal => signal.Id == "do.reverse").Value);
        Assert.False(afterRejected.Signals.Single(signal => signal.Id == "do.run").Value);

        Assert.True(hub.SetDigitalInput("di.guard", true, SignalWriteOwner.Manual).IsAccepted);
        long beforeAcceptedRevision = hub.CaptureSnapshot().Revision;
        DigitalOutputPairWriteResult accepted = hub.SetDigitalOutputPairAtomically(
            "do.reverse",
            true,
            "do.run",
            true,
            SignalWriteOwner.Manual);
        SignalHubSnapshot afterAccepted = hub.CaptureSnapshot();

        Assert.True(accepted.IsAccepted);
        Assert.Equal(2, accepted.ChangedSignalCount);
        Assert.Equal(beforeAcceptedRevision + 1, accepted.Revision);
        Assert.Equal(accepted.Revision, afterAccepted.Revision);
        Assert.True(afterAccepted.Signals.Single(signal => signal.Id == "do.reverse").Value);
        Assert.True(afterAccepted.Signals.Single(signal => signal.Id == "do.run").Value);
    }

    [Fact]
    public void DigitalInputOverride_DropsAnInterlockedActiveOutput()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.guard", ChannelKind.DigitalInput, 1),
            Signal("do.active", ChannelKind.DigitalOutput, 0, "di.guard")
        }).Hub!;
        hub.SetDigitalOutput("do.active", true, SignalWriteOwner.EmbeddedSequence);

        DigitalInputOverrideResult forced = hub.SetDigitalInputOverride("di.guard", false);

        Assert.True(forced.IsAccepted);
        Assert.False(hub.ReadDigitalSignal("do.active").Value);
    }

    [Fact]
    public void Reset_RestoresAllInitialValuesWithOneRevision()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.start", ChannelKind.DigitalInput, 0),
            Signal("do.done", ChannelKind.DigitalOutput, 1)
        }).Hub!;
        hub.SetDigitalInput("di.start", true, SignalWriteOwner.Manual);
        hub.SetDigitalOutput("do.done", false, SignalWriteOwner.EmbeddedSequence);
        var beforeReset = hub.CaptureSnapshot();

        var reset = hub.Reset();
        var afterReset = hub.CaptureSnapshot();
        var noOpReset = hub.Reset();

        Assert.Equal(2, reset.ChangedSignalCount);
        Assert.Equal(beforeReset.Revision + 1, reset.Revision);
        Assert.False(afterReset.Signals.Single(signal => signal.Id == "di.start").Value);
        Assert.True(afterReset.Signals.Single(signal => signal.Id == "do.done").Value);
        Assert.False(noOpReset.StateChanged);
        Assert.Equal(reset.Revision, noOpReset.Revision);
    }

    [Fact]
    public void DigitalInputOverride_RetainsNominalWritesAndRestoresThemWhenCleared()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.sensor", ChannelKind.DigitalInput, 0)
        }).Hub!;

        DigitalInputOverrideResult activated = hub.SetDigitalInputOverride("di.sensor", false);
        SignalWriteResult nominalWrite = hub.SetDigitalInput(
            "di.sensor",
            true,
            SignalWriteOwner.SimulationComponent);
        DigitalSignalSnapshot forcedSnapshot = hub.CaptureSnapshot().Signals.Single();
        DigitalInputOverrideResult cleared = hub.SetDigitalInputOverride("di.sensor", null);

        Assert.True(activated.IsAccepted);
        Assert.True(activated.OverrideChanged);
        Assert.False(activated.ValueChanged);
        Assert.Equal(false, forcedSnapshot.OverrideValue);
        Assert.True(nominalWrite.IsAccepted);
        Assert.False(nominalWrite.StateChanged);
        Assert.True(nominalWrite.RequestedValue);
        Assert.False(nominalWrite.CurrentValue);
        Assert.True(forcedSnapshot.NominalValue);
        Assert.True(cleared.IsAccepted);
        Assert.True(cleared.OverrideChanged);
        Assert.True(cleared.ValueChanged);
        Assert.True(hub.ReadDigitalSignal("di.sensor").Value);
    }

    [Fact]
    public void Reset_ClearsDigitalInputOverrideAndNominalValue()
    {
        var hub = DeterministicSignalHub.Create(new[]
        {
            Signal("di.sensor", ChannelKind.DigitalInput, 0)
        }).Hub!;
        hub.SetDigitalInput("di.sensor", true, SignalWriteOwner.Manual);
        hub.SetDigitalInputOverride("di.sensor", true);

        SignalHubResetResult reset = hub.Reset();
        DigitalInputOverrideResult clearAfterReset = hub.SetDigitalInputOverride("di.sensor", null);

        Assert.True(reset.StateChanged);
        Assert.False(hub.ReadDigitalSignal("di.sensor").Value);
        Assert.Null(hub.CaptureSnapshot().Signals.Single().OverrideValue);
        Assert.False(clearAfterReset.OverrideChanged);
        Assert.False(clearAfterReset.ValueChanged);
    }

    private static ChannelDefinition Signal(
        string id,
        ChannelKind kind,
        double initialValue,
        params string[] interlockIds) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = initialValue,
            InterlockIds = interlockIds.ToList()
        };
}
