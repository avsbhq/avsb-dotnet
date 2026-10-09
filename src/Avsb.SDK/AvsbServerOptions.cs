using Avsb.SDK.Core;
using Microsoft.Extensions.Logging;

namespace Avsb.SDK;

/// <summary>
/// Construction-time options for <see cref="AvsbServer"/>. Every option has a documented default;
/// only <see cref="SdkKey"/> is required.
///
/// The properties are settable, not init-only, so the ASP.NET Core registration
/// <c>services.AddAvsb(options => options.SdkKey = ...)</c> compiles: with init-only setters that
/// documented form was a compile error. The server copies the options when it is constructed, so
/// changing this object afterwards does not reach a running server.
/// </summary>
public sealed record AvsbServerOptions
{
    /// <summary>
    /// SDK key issued by the A vs B dashboard (Environments in the project sidebar). Looks like
    /// sdk_production_ttqm0eaj4vth1krcb2xn.
    /// </summary>
    public string SdkKey { get; set; } = string.Empty;

    /// <summary>
    /// Serve the datafile from a different origin, for a self-hosted proxy or local development.
    /// The path shape is unchanged: {CdnHost}/{SdkKey}/datafile.json. Default:
    /// https://cdn.avsb.cloud.
    /// </summary>
    public string? CdnHost { get; set; }

    /// <summary>
    /// Override the whole datafile URL. Prefer <see cref="CdnHost"/> unless you are pointing at a
    /// file this SDK did not publish.
    ///
    /// This replaces the old Endpoint option, which took a base Uri and combined it with a
    /// leading-slash relative path. That combine silently discarded the base path, so a customer
    /// who correctly configured "https://host/v1" still fetched "https://host/datafile/{key}".
    /// </summary>
    public string? DatafileUrl { get; set; }

    /// <summary>How often the background loop refetches the datafile. Default 30 seconds.</summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>Per-request HTTP timeout for datafile fetches. Default 10 seconds.</summary>
    public TimeSpan? FetchTimeout { get; set; }

    /// <summary>
    /// Turn the background refresh loop off. Default on. With it off, a flag change needs a
    /// process restart.
    /// </summary>
    public bool Polling { get; set; } = true;

    /// <summary>
    /// Turn event sending off. Default on. With it off, Track and exposure recording become
    /// no-ops that say so, and server-side experiments produce no results.
    /// </summary>
    public bool Tracking { get; set; } = true;

    /// <summary>
    /// Serve this datafile immediately, so flags work before the first fetch completes and keep
    /// working if that fetch fails. Pass the JSON text; a later fetch replaces it.
    ///
    /// Without this, a transient network error at deploy time used to leave the SDK on defaults
    /// with no retry until the process restarted.
    /// </summary>
    public string? Bootstrap { get; set; }

    /// <summary>
    /// Force flags for this process: flag key to VARIATION KEY, resolved against the datafile so
    /// the served value and its key are real. A variation key that does not exist is logged and
    /// ignored rather than serving something invented.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RuntimeOverrides { get; set; }

    /// <summary>Optional Microsoft.Extensions.Logging logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Keeps users in the variation they were first assigned. Default: not configured.</summary>
    public IStickyBucketService? StickyBucketService { get; set; }

    /// <summary>
    /// Learned bandit models. Default: none, in which case a bandit rule still dispatches using a
    /// deterministic cold-start model derived from the datafile.
    /// </summary>
    public IAvsbBanditModelStore? BanditModelStore { get; set; }

    /// <summary>
    /// How many decision-log entries to retain. Default 1000; zero turns recording off. The log is
    /// bounded so a service that never reads it pays a fixed cost instead of leaking.
    /// </summary>
    public int DecisionLogCapacity { get; set; } = DecisionLog.DefaultCapacity;
}
