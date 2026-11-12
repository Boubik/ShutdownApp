using System.Globalization;

namespace IdleShutdown.AgentApp;

internal sealed record UiStrings(
    string WindowTitle,
    string Title,
    string Status,
    string CountdownCaption,
    string Hint,
    string ContinueButton,
    string UrgentStatus,
    string MaintenancePreWarningStatus,
    string MaintenanceActiveCaption,
    string MaintenanceElapsedCaption,
    string MaintenanceStatus,
    string ServiceContactFailed,
    string ComputerWillNotShutdown,
    string TechnicalDetail);

internal static class LocalizedText
{
    private static readonly UiStrings English = new(
        WindowTitle: "Automatic shutdown",
        Title: "Automatic shutdown",
        Status: "This computer has not been used for some time. " +
                "Continuing your work will cancel the shutdown immediately.",
        CountdownCaption: "Shutting down in",
        Hint: "Move the mouse, press any key, or use the button below.",
        ContinueButton: "Continue working",
        UrgentStatus: "Shutdown is imminent. Move the mouse or use the button to continue.",
        MaintenancePreWarningStatus: "Updates are starting now. The computer will shut down after the countdown and updates finish.",
        MaintenanceActiveCaption: "UPDATES IN PROGRESS",
        MaintenanceElapsedCaption: "Waiting: {0}",
        MaintenanceStatus: "Windows Update and Chocolatey are running. " +
                           "The computer will shut down once they finish. You can still cancel.",
        ServiceContactFailed: "The automatic shutdown service could not be contacted.",
        ComputerWillNotShutdown: "The computer will not be shut down.",
        TechnicalDetail: "Technical detail");

    private static readonly UiStrings Czech = new(
        WindowTitle: "Automatické vypnutí",
        Title: "Automatické vypnutí",
        Status: "Tento počítač nebyl delší dobu používán. " +
                "Pokračováním v práci vypnutí okamžitě zrušíte.",
        CountdownCaption: "Vypnutí za",
        Hint: "Pohněte myší, stiskněte libovolnou klávesu nebo použijte tlačítko níže.",
        ContinueButton: "Pokračovat v práci",
        UrgentStatus: "Vypnutí je bezprostřední. Pohněte myší nebo pokračujte tlačítkem.",
        MaintenancePreWarningStatus: "Aktualizace se spouštějí už nyní. Počítač se vypne po odpočtu a dokončení aktualizací.",
        MaintenanceActiveCaption: "PROBÍHAJÍ AKTUALIZACE",
        MaintenanceElapsedCaption: "Čekání: {0}",
        MaintenanceStatus: "Windows Update a Chocolatey pracují. " +
                           "Po dokončení se počítač vypne. Vypnutí lze stále zrušit.",
        ServiceContactFailed: "Nepodařilo se kontaktovat službu automatického vypnutí.",
        ComputerWillNotShutdown: "Počítač nebude vypnut.",
        TechnicalDetail: "Technický detail");

    private static readonly UiStrings German = new(
        WindowTitle: "Automatisches Herunterfahren",
        Title: "Automatisches Herunterfahren",
        Status: "Dieser Computer wurde längere Zeit nicht verwendet. " +
                "Wenn Sie weiterarbeiten, wird das Herunterfahren sofort abgebrochen.",
        CountdownCaption: "Herunterfahren in",
        Hint: "Bewegen Sie die Maus, drücken Sie eine Taste oder verwenden Sie die Schaltfläche unten.",
        ContinueButton: "Weiterarbeiten",
        UrgentStatus: "Das Herunterfahren steht unmittelbar bevor. Bewegen Sie die Maus oder verwenden Sie die Schaltfläche.",
        MaintenancePreWarningStatus: "Updates starten jetzt. Der PC fährt nach dem Countdown und Abschluss der Updates herunter.",
        MaintenanceActiveCaption: "UPDATES WERDEN INSTALLIERT",
        MaintenanceElapsedCaption: "Wartezeit: {0}",
        MaintenanceStatus: "Windows Update und Chocolatey sind aktiv. " +
                           "Danach fährt der PC herunter. Sie können noch abbrechen.",
        ServiceContactFailed: "Der Dienst für das automatische Herunterfahren konnte nicht erreicht werden.",
        ComputerWillNotShutdown: "Der Computer wird nicht heruntergefahren.",
        TechnicalDetail: "Technisches Detail");

    private static readonly UiStrings Spanish = new(
        WindowTitle: "Apagado automático",
        Title: "Apagado automático",
        Status: "Este equipo no se ha utilizado durante un tiempo. " +
                "Si continúa trabajando, el apagado se cancelará inmediatamente.",
        CountdownCaption: "Apagado en",
        Hint: "Mueva el ratón, pulse cualquier tecla o use el botón de abajo.",
        ContinueButton: "Continuar trabajando",
        UrgentStatus: "El apagado es inminente. Mueva el ratón o use el botón para continuar.",
        MaintenancePreWarningStatus: "Las actualizaciones comienzan ahora. El equipo se apagará cuando termine la cuenta y las actualizaciones.",
        MaintenanceActiveCaption: "INSTALANDO ACTUALIZACIONES",
        MaintenanceElapsedCaption: "Esperando: {0}",
        MaintenanceStatus: "Windows Update y Chocolatey siguen trabajando. " +
                           "El equipo se apagará al terminar. Todavía puede cancelar.",
        ServiceContactFailed: "No se pudo contactar con el servicio de apagado automático.",
        ComputerWillNotShutdown: "El equipo no se apagará.",
        TechnicalDetail: "Detalle técnico");

    public static UiStrings Current =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant()
        switch
        {
            "cs" => Czech,
            "de" => German,
            "es" => Spanish,
            _ => English
        };
}
