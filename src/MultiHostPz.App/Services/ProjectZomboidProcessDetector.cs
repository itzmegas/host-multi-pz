using System.Diagnostics;

namespace MultiHostPz.App.Services;

public interface IProjectZomboidProcessDetector
{
    bool IsProjectZomboidRunning();
}

public sealed class ProjectZomboidProcessDetector : IProjectZomboidProcessDetector
{
    private static readonly string[] KnownProcessNames =
    [
        "ProjectZomboid64",
        "ProjectZomboid32",
        "ProjectZomboid"
    ];

    public bool IsProjectZomboidRunning()
    {
        return KnownProcessNames.Any(processName =>
            Process.GetProcessesByName(processName).Length > 0);
    }
}
