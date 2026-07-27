namespace NgStationTool;

internal static class TrayClosePolicy
{
    public static bool ShouldMinimizeToTray(CloseReason reason, bool explicitExitRequested)
        => !explicitExitRequested
           && reason != CloseReason.WindowsShutDown
           && reason != CloseReason.TaskManagerClosing
           && reason != CloseReason.ApplicationExitCall;
}
