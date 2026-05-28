using Microsoft.Extensions.Logging;

namespace Avsb.SDK;

/// <summary>
/// Construction-time options for AvsbServer. Mirrors spec §9.7 AvsbServerOptions.
/// </summary>
public sealed record AvsbServerOptions
{
    /// <summary>SDK key issued by the AvsB dashboard.</summary>
    public string SdkKey { get; init; } = string.Empty;

    /// <summary>Override the datafile endpoint (default: https://sdk.avsb.cloud/v1).</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Polling interval (default: 30 seconds).</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>Optional Microsoft.Extensions.Logging logger.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Optional sticky bucket service for consistent re-bucketing.</summary>
    public IStickyBucketService? StickyBucketService { get; init; }
}
