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
