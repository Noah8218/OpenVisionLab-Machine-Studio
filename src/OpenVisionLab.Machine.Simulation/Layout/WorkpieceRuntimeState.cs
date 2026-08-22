namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class WorkpieceRuntimeState : LayoutComponentRuntimeState
{
    public WorkpieceRuntimeState(WorkpieceRuntimeConfiguration configuration)
        : base(configuration)
    {
        WorkpieceConfiguration = configuration;
    }

    public WorkpieceRuntimeConfiguration WorkpieceConfiguration { get; }
    public double CarrierPosition { get; private set; }
    public string? TransferOwnerId { get; private set; }
    public WaferHandlerOwnershipState? TransferOwnershipState { get; private set; }

    public void Tick(ConveyorRuntimeState conveyor)
    {
        if (!conveyor.IsRunning)
        {
            UpdateCarrierPosition(conveyor);
            return;
        }

        double radians = conveyor.RotationDegrees * Math.PI / 180d;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double deltaX = X - conveyor.X;
        double deltaY = Y - conveyor.Y;
        double localX = (deltaX * cosine) + (deltaY * sine);
        double localY = (-deltaX * sine) + (deltaY * cosine);
        double maximumTravel =
            (conveyor.Configuration.Size.Width - Configuration.Size.Width) / 2d;
        double direction = conveyor.Direction == ConveyorDirection.Forward ? 1d : -1d;
        localX = Math.Clamp(
            localX + (conveyor.ConveyorConfiguration.TravelPerTick * direction),
            -maximumTravel,
            maximumTravel);
        X = conveyor.X + (localX * cosine) - (localY * sine);
        Y = conveyor.Y + (localX * sine) + (localY * cosine);
        CarrierPosition = localX;
    }

    public void UpdateCarrierPosition(ConveyorRuntimeState conveyor)
    {
        double radians = conveyor.RotationDegrees * Math.PI / 180d;
        CarrierPosition = ((X - conveyor.X) * Math.Cos(radians))
            + ((Y - conveyor.Y) * Math.Sin(radians));
    }

    public void ApplyTransferOwnership(
        string ownerId,
        WaferHandlerOwnershipState ownershipState)
    {
        TransferOwnerId = ownerId;
        TransferOwnershipState = ownershipState;
    }

    public override void Reset()
    {
        base.Reset();
        TransferOwnerId = null;
        TransferOwnershipState = null;
    }

    public override LayoutComponentSnapshot CaptureSnapshot() =>
        new(
            Configuration.Id,
            Configuration.Name,
            Configuration.Kind,
            X,
            Y,
            RotationDegrees,
            Configuration.Size.Width,
            Configuration.Size.Height,
            null,
            null,
            CarrierComponentId: WorkpieceConfiguration.ConveyorComponentId,
            CarrierPosition: CarrierPosition,
            WorkpieceType: WorkpieceConfiguration.Type,
            InspectionState: WorkpieceConfiguration.InspectionState,
            TransferOwnerId: TransferOwnerId,
            TransferOwnershipState: TransferOwnershipState);
}
