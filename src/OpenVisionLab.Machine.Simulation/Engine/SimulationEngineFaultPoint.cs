namespace OpenVisionLab.Machine.Simulation.Engine;

internal enum SimulationEngineFaultPoint
{
    BeforeCommandApplication,
    AfterCommandApplication,
    BeforeTick,
    AfterTick,
    BeforeEventPublication,
    BeforeSnapshotPublication,
    AfterSnapshotPublication
}
