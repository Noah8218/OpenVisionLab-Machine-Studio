namespace OpenVisionLab.MachineStudio;

internal static class SmokeRoundTripScenario
{
    internal const string RoundTripStageId = "stage-1";
    internal const double RoundTripStageX = 70.0;
    internal const string RoundTripCylinderId = "cylinder-1";
    internal const string RoundTripAlignedComponentId = "sensor-1";
    internal const string RoundTripCylinderName = "Stopper Cylinder RT";
    internal const double RoundTripCylinderRotation = 15.0;
    internal const double RoundTripCylinderWidth = 110.0;
    internal const double RoundTripCylinderHeight = 44.0;
    internal const int RoundTripCylinderExtendDuration = 150;
    internal const double RoundTripCylinderStroke = 65.0;
    internal const double RoundTripAxisMaxVelocity = 175.0;
    internal const double RoundTripAxisMaxAcceleration = 650.0;
    internal const double RoundTripAxisMaxDeceleration = 575.0;
    internal const double RoundTripAxisFollowingErrorLimit = 0.08;
    internal const double RoundTripAlignedComponentX = 310.0;
    internal const string RoundTripStepId = "cycle-active-on";
    internal const string RoundTripStepName = "Cycle Active On [Roundtrip]";
    internal const string RoundTripStepCheckpointTargetId = RoundTripCylinderId;
    internal const string RoundTripStepCheckpointState = "Retracted";
    internal const string RoundTripScenarioProfileId = "fault-injection";
    internal const int RoundTripScenarioSeed = 4242;
    internal const int RoundTripScenarioDuration = 37;
    internal const string RoundTripScenarioTargetId = "conveyor-1";
}
