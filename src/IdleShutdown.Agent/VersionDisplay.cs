using System.Reflection;
using System.Text.RegularExpressions;

namespace IdleShutdown.AgentApp;

internal static class VersionDisplay
{
    // AssemblyVersion is numeric only. InformationalVersion retains SemVer
    // prerelease labels, which are required for correctly identifying beta builds.
    public static string FromAssembly(Assembly assembly) =>
        Format(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);

    internal static string Format(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            // SDK can append SourceRevisionId (+commit). Do not show it in the popup.
            var semver = informationalVersion.Split('+', 2)[0].Trim();
            if (Regex.IsMatch(
                    semver,
                    @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$",
                    RegexOptions.CultureInvariant))
                return $"v{semver}";
        }

        return assemblyVersion is null
            ? string.Empty
            : $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }
}
