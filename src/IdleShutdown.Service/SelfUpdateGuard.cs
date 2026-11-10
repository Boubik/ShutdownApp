namespace IdleShutdown.ServiceApp;

// A separate SYSTEM scheduled-task helper owns this lock throughout the
// complete Chocolatey self-upgrade, including the service restart. Never
// begin maintenance or shut down while it is held.
internal static class SelfUpdateGuard
{
    private static readonly string GuardPath =
        Path.Combine(AppConfig.BaseDirectory, "selfupdate.lock");

    public static bool IsExternalUpgradeRunning()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.BaseDirectory);
            using var probe = new FileStream(
                GuardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Fail closed if the guard cannot be checked.
            return true;
        }
    }
}
