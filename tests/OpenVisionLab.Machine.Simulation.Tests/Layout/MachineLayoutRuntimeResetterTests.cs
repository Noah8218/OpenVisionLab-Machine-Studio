using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.Machine.Simulation.Layout;
using Xunit;

namespace OpenVisionLab.Machine.Simulation.Tests;

public sealed class MachineLayoutRuntimeResetterTests
{
    [Fact]
    public void ResetRestoresComponentAndCarrierStateBeforeSignalCallback()
    {
        var conveyor = new ConveyorRuntimeState(new ConveyorRuntimeConfiguration(
            "conveyor-1",
            "Conveyor",
            "do.run",
            "do.reverse",
            10,
            0.1,
            new LayoutRuntimeTransform(100, 50),
            new LayoutRuntimeSize(100, 40)));
        var workpiece = new WorkpieceRuntimeState(new WorkpieceRuntimeConfiguration(
            "workpiece-1",
            "Workpiece",
            "Wafer",
            "conveyor-1",
            WorkpieceInspectionState.Pending,
            new LayoutRuntimeTransform(100, 50),
            new LayoutRuntimeSize(10, 10)));
        conveyor.Tick(runCommand: true, reverseCommand: false);
        conveyor.X = 125;
        workpiece.X = 130;
        workpiece.Y = 70;
        var resetter = new MachineLayoutRuntimeResetter(
            new Dictionary<string, LayoutComponentRuntimeState>
            {
                [conveyor.Configuration.Id] = conveyor,
                [workpiece.Configuration.Id] = workpiece
            },
            new LayoutComponentRuntimeState[] { conveyor, workpiece },
            new[] { workpiece },
            Array.Empty<LoadLockRuntimeState>(),
            Array.Empty<WaferHandlerRuntimeState>(),
            Array.Empty<InspectionSortRouterRuntimeState>(),
            Array.Empty<InspectionHandoffRuntimeState>(),
            Array.Empty<OhtHandoffRuntimeState>(),
            Array.Empty<PrealignerRuntimeState>());
        bool callbackObserved = false;

        resetter.Reset(() =>
        {
            callbackObserved = true;
            Assert.Equal(100, conveyor.X);
            Assert.Equal(50, workpiece.Y);
            Assert.False(conveyor.IsRunning);
            Assert.Equal(0, workpiece.CarrierPosition);
        });

        Assert.True(callbackObserved);
        Assert.Equal(100, workpiece.X);
        Assert.Equal(50, workpiece.Y);
    }
}
