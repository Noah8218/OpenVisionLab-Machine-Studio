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
    public void Create_AnalogOrDuplicateSignal_IsRejected()
    {
        var analog = DeterministicSignalHub.Create(new[]
        {
            Signal("ai.height", ChannelKind.AnalogInput, 0)
        });
        var duplicate = DeterministicSignalHub.Create(new[]
        {
            Signal("di.start", ChannelKind.DigitalInput, 0),
            Signal("di.start", ChannelKind.DigitalInput, 1)
        });

        Assert.Equal(SignalHubErrorCode.UnsupportedChannelKind, analog.ErrorCode);
        Assert.Equal(SignalHubErrorCode.DuplicateChannelId, duplicate.ErrorCode);
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

    private static ChannelDefinition Signal(string id, ChannelKind kind, double initialValue) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            InitialValue = initialValue
        };
}
