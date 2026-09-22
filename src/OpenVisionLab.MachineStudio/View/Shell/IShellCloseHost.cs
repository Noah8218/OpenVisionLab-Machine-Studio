namespace OpenVisionLab.MachineStudio.View.Shell;

public interface IShellCloseHost
{
    Task<bool> RequestCloseAsync();

    void PresentCloseFailure(Exception exception);
}
