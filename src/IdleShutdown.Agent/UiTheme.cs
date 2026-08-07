using Microsoft.Win32;

namespace IdleShutdown.AgentApp;

internal sealed record UiPalette(
    Color CardBackground,
    Color IconBackground,
    Color PrimaryText,
    Color SecondaryText,
    Color MutedText,
    Color ProgressTrack,
    Color Accent,
    Color AccentHover,
    Color Danger,
    Color ButtonText);

internal static class UiTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly UiPalette Light = new(
        CardBackground: Color.FromArgb(250, 251, 253),
        IconBackground: Color.FromArgb(232, 241, 255),
        PrimaryText: Color.FromArgb(22, 30, 42),
        SecondaryText: Color.FromArgb(86, 98, 114),
        MutedText: Color.FromArgb(102, 113, 128),
        ProgressTrack: Color.FromArgb(224, 230, 238),
        Accent: Color.FromArgb(24, 102, 210),
        AccentHover: Color.FromArgb(20, 87, 180),
        Danger: Color.FromArgb(190, 43, 43),
        ButtonText: Color.White);

    private static readonly UiPalette Dark = new(
        CardBackground: Color.FromArgb(31, 34, 40),
        IconBackground: Color.FromArgb(36, 51, 73),
        PrimaryText: Color.FromArgb(241, 244, 248),
        SecondaryText: Color.FromArgb(184, 192, 204),
        MutedText: Color.FromArgb(164, 174, 188),
        ProgressTrack: Color.FromArgb(67, 73, 84),
        Accent: Color.FromArgb(82, 158, 255),
        AccentHover: Color.FromArgb(105, 174, 255),
        Danger: Color.FromArgb(255, 108, 108),
        ButtonText: Color.White);

    public static UiPalette Current =>
        IsDarkModeEnabled()
            ? Dark
            : Light;

    private static bool IsDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue("AppsUseLightTheme");

            return value is int appsUseLightTheme &&
                   appsUseLightTheme == 0;
        }
        catch
        {
            return false;
        }
    }
}
