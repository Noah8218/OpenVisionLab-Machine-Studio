namespace OpenVisionLab.Machine.Simulation.Layout;

internal sealed class WorkpieceRuntimeState : LayoutComponentRuntimeState
{
    public WorkpieceRuntimeState(WorkpieceRuntimeConfiguration configuration)
        : base(configuration)
    {
        WorkpieceConfiguration = configuration;
        IsPresent = configuration.InitiallyPresent;
    }

    public WorkpieceRuntimeConfiguration WorkpieceConfiguration { get; }
    public bool IsPresent { get; private set; }
    public double CarrierPosition { get; private set; }
    public string? TransferOwnerId { get; private set; }
    public string? WorkpieceInstanceId { get; private set; }
    public WaferHandlerOwnershipState? TransferOwnershipState { get; private set; }
    private bool IsOnAuthoredCarrier =>
        TransferOwnershipState is null
            or WaferHandlerOwnershipState.Source
            or WaferHandlerOwnershipState.Destination;

    public void Tick(ConveyorRuntimeState conveyor)
    {
        if (!IsPresent || !IsOnAuthoredCarrier)
        {
            return;
        }

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

    public void AttachTransferOwner(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (TransferOwnerId is not null)
        {
            throw new InvalidOperationException(
                $"Workpiece '{Configuration.Id}' already has transfer owner '{TransferOwnerId}'.");
        }

        TransferOwnerId = ownerId;
        TransferOwnershipState = WaferHandlerOwnershipState.Source;
    }

    public void ApplyTransferTransition(WaferHandlerOwnershipState ownershipState)
    {
        if (TransferOwnerId is null)
        {
            throw new InvalidOperationException(
                $"Workpiece '{Configuration.Id}' has no transfer owner.");
        }

        TransferOwnershipState = ownershipState;
    }

    public void AssignWorkpieceInstanceId(string workpieceInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workpieceInstanceId);
        if (!IsPresent)
        {
            throw new InvalidOperationException($"Workpiece '{Configuration.Id}' is not present.");
        }
        if (WorkpieceInstanceId is not null)
        {
            throw new InvalidOperationException(
                $"Workpiece '{Configuration.Id}' already has instance id '{WorkpieceInstanceId}'.");
        }

        WorkpieceInstanceId = workpieceInstanceId;
    }

    public bool TryFeed(string workpieceInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workpieceInstanceId);
        if (IsPresent || TransferOwnerId is not null || WorkpieceInstanceId is not null)
        {
            return false;
        }

        base.Reset();
        IsPresent = true;
        TransferOwnershipState = null;
        WorkpieceInstanceId = workpieceInstanceId;
        return true;
    }

    public bool TryEject()
    {
        if (!IsPresent || TransferOwnerId is not null)
        {
            return false;
        }

        IsPresent = false;
        WorkpieceInstanceId = null;
        return true;
    }

    public override void Reset()
    {
        base.Reset();
        IsPresent = WorkpieceConfiguration.InitiallyPresent;
        WorkpieceInstanceId = null;
        TransferOwnershipState = TransferOwnerId is null
            ? null
            : WaferHandlerOwnershipState.Source;
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
            CarrierComponentId: IsOnAuthoredCarrier
                ? WorkpieceConfiguration.ConveyorComponentId
                : null,
            CarrierPosition: IsOnAuthoredCarrier
                ? CarrierPosition
                : null,
            WorkpieceType: WorkpieceConfiguration.Type,
            InspectionState: WorkpieceConfiguration.InspectionState,
            TransferOwnerId: TransferOwnerId,
            TransferOwnershipState: TransferOwnershipState,
            WorkpieceInstanceId: WorkpieceInstanceId,
            IsWorkpiecePresent: IsPresent);
}
