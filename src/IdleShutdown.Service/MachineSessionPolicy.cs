namespace IdleShutdown.ServiceApp;

internal static class MachineSessionPolicy
{
    public static bool GetEffectiveLockedState(
        bool wtsIsLocked,
        bool serviceObservedLock,
        bool? agentIsLocked)
    {
        // WTS/service lock and disconnect notifications are authoritative.
        // The per-session agent may add a lock signal when WTS information is
        // incomplete, but an unreliable UNLOCKED heartbeat must never undo a
        // lock already observed by the service.
        return
            wtsIsLocked ||
            serviceObservedLock ||
            agentIsLocked == true;
    }

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
