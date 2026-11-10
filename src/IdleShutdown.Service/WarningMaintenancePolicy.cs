namespace IdleShutdown.ServiceApp;

internal static class WarningMaintenancePolicy
{
    // The warning begins maintenance. A test dry-run must never install updates.
    public static bool StartDuringWarning(bool maintenanceEnabled, bool dryRun) =>
        maintenanceEnabled && !dryRun;

    // The agent must still confirm its countdown expired before shutdown.
    public static bool InteractiveShutdownConfirmed(bool confirmed) =>
        confirmed;
}
