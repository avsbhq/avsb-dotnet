using System.Reflection;

namespace Avsb.SDK;

/// <summary>
/// Who this SDK says it is on the wire.
///
/// Both values go into the heartbeat body (sdkType, sdkVersion), which is what drives the
/// Connected, Stale, and Not connected states on the dashboard's Environments page.
///
/// The version is READ FROM ASSEMBLY METADATA, not from a literal in this file. MSBuild stamps it
/// from the csproj Version property, so it cannot drift from the published package the way a
/// hand-maintained constant does the first time someone forgets to bump it.
/// </summary>
public static class SdkMetadata
{
    /// <summary>The NuGet package id, exactly as published.</summary>
    public const string SdkType = "Avsb.SDK";

    private const string FallbackVersion = "0.0.0-dev";

    private static readonly string Version = ReadVersion();

    /// <summary>The published package version.</summary>
    public static string SdkVersion => Version;

    /// <summary>Value for the HTTP User-Agent header.</summary>
    public static string UserAgent => "avsb-dotnet/" + Version;

    private static string ReadVersion()
    {
        Assembly assembly = typeof(SdkMetadata).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+{commit sha}" when SourceLink is on; the wire wants the version.
            int plus = informational!.IndexOf('+');
            return plus > 0 ? informational.Substring(0, plus) : informational;
        }

        Version? assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null ? FallbackVersion : assemblyVersion.ToString(3);
    }
}
