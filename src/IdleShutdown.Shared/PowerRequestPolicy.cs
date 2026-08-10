namespace IdleShutdown.Shared;

internal static class PowerRequestPolicy
{
    private const uint EsDisplayRequired = 0x00000002;

    public static bool ShouldPauseForPresentation(
        uint executionState,
        bool presentationProtectionEnabled)
    {
        return
            presentationProtectionEnabled &&
            (executionState & EsDisplayRequired) != 0;
    }
}
