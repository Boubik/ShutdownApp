namespace IdleShutdown.AgentApp;

internal static class ForegroundProtectionPolicy
{
    private static readonly string[] ProcessNameTokens =
    [
        "setup",
        "install",
        "unins",
        "updater",
        "update",
        "upgrade"
    ];

    private static readonly string[] InstallerTitlePrefixes =
    [
        "Průvodce instalací",
        "Instalace ",
        "Odinstalace ",
        "Aktualizace ",
        "Setup ",
        "Setup -",
        "Installing ",
        "Installer ",
        "Uninstall ",
        "Installation -",
        "Update ",
        "Updater ",
        "Upgrade ",
        "Installationsassistent",
        "Installationsprogramm",
        "Deinstallation ",
        "Aktualisierung ",
        "Asistente de instalación",
        "Instalación ",
        "Instalador ",
        "Desinstalación ",
        "Actualización "
    ];

    public static bool IsInstallerOrUpdater(
        string? processName,
        string? windowTitle)
    {
        if (
            !string.IsNullOrWhiteSpace(processName) &&
            ProcessNameTokens.Any(
                token => processName.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return
            !string.IsNullOrWhiteSpace(windowTitle) &&
            InstallerTitlePrefixes.Any(
                prefix => windowTitle.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase));
    }
}
