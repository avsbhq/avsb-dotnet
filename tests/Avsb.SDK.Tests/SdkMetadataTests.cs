using System.Xml.Linq;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// The version this SDK reports (heartbeat sdkVersion, User-Agent) is the version in the csproj,
/// the one NuGet publishes. A hand-kept constant drifted from the package in the JavaScript SDKs
/// (N0-9); here MSBuild stamps the assembly, and this test proves the stamp reaches the wire value.
/// </summary>
public sealed class SdkMetadataTests
{
    private static string CsprojVersion()
    {
        DirectoryInfo? cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && cursor is not null; i++, cursor = cursor.Parent)
        {
            string candidate = Path.Combine(cursor.FullName, "src", "Avsb.SDK", "Avsb.SDK.csproj");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate).Descendants("Version").Single().Value;
            }
        }
        throw new InvalidOperationException(
            "src/Avsb.SDK/Avsb.SDK.csproj not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void ReportedVersion_IsTheCsprojVersion()
    {
        SdkMetadata.SdkVersion.Should().Be(CsprojVersion());
    }

    [Fact]
    public void UserAgent_CarriesTheSameVersion()
    {
        SdkMetadata.UserAgent.Should().Be("avsb-dotnet/" + CsprojVersion());
    }

    [Fact]
    public void ReportedVersion_HasNoBuildMetadataSuffix()
    {
        // SourceLink appends "+{commit}" to the informational version; the wire wants the version.
        SdkMetadata.SdkVersion.Should().NotContain("+");
        SdkMetadata.SdkType.Should().Be("Avsb.SDK");
    }
}
