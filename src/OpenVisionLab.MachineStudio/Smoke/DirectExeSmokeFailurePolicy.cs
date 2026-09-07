namespace OpenVisionLab.MachineStudio;

internal readonly record struct DirectExeSmokeFailureCheck(bool IsValid, int ExitCode);

internal static class DirectExeSmokeFailurePolicy
{
    internal static int SelectExitCode(params DirectExeSmokeFailureCheck[] checks)
    {
        foreach (var check in checks)
        {
            if (!check.IsValid)
            {
                return check.ExitCode;
            }
        }

        return 0;
    }
}
