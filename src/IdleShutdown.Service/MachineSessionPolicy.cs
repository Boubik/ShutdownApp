namespace IdleShutdown.ServiceApp;

internal static class MachineSessionPolicy
{
    public static bool CanApplyLockedTimeout(
        IEnumerable<bool> lockedStates)
    {
        var anyLoggedOnSession = false;

        foreach (var isLocked in lockedStates)
        {
            anyLoggedOnSession = true;

            if (!isLocked)
            {
                return false;
            }
        }

        return anyLoggedOnSession;
    }
}
