namespace IdleShutdown.ServiceApp;

internal static class SessionInputActivity
{
    public static bool HasChanged(
        DateTime? previous,
        DateTime? current)
    {
        return current.HasValue &&
               (
                   !previous.HasValue ||
                   previous.Value != current.Value
               );
    }
}
