namespace Avsb.SDK;

/// <summary>
/// Data-plane endpoint contract (SDK/API DX overhaul spec §1.1).
///
/// Single source of truth for every network target this SDK talks to. Mirror of
/// packages/avsb-core/src/endpoints.ts; both are locked by the "endpoints" conformance
/// fixture family (packages/avsb-conformance/fixtures/endpoints/manifest.json).
///
/// Nothing in this SDK may compose a data-plane URL by hand. Change a URL here and the
/// fixture changes with it, in the same commit, or every SDK's conformance run fails.
///
/// Credential posture:
///  - Datafile and preview reads are keyless by path. The SDK key is the path segment:
///    no header, no query string, no Authorization.
///  - Event sends carry sdkKey in the JSON body envelope.
///  - The stream ticket request is the one read that takes the key as a query parameter.
///  - No Authorization header anywhere on the data plane.
/// </summary>
public static class Endpoints
{
    /// <summary>
    /// Default hosts. Overridable for self-hosted proxies, local dev, and tests. Nothing here
    /// points at a host that does not exist: datafile.avsb.cloud, stream.avsb.cloud, and
    /// sdk.avsb.cloud are NOT real and must never appear as defaults.
    /// </summary>
    public const string DefaultCdnHost = "https://cdn.avsb.cloud";

    public const string DefaultCollectHost = "https://ingest.avsb.cloud";

    public const string DefaultAppHost = "https://app.avsb.cloud";

    /// <summary>Ingestion paths. Absolute paths appended to a collect ORIGIN.</summary>
    public const string CollectPathSingle = "/v1/collect";

    public const string CollectPathBatch = "/v1/collect/batch";

    public const string CollectPathMetricBatch = "/v1/collect/metric-batch";

    public const string CollectPathOrders = "/v1/collect/orders";

    public const string CollectPathErrors = "/v1/collect/errors";

    public const string CollectPathPing = "/v1/ping";

    public const string CollectPathGeo = "/v1/geo";

    public const string StreamTicketPath = "/api/sdk/stream";

    public const string HeartbeatPath = "/api/sdk/heartbeat";

    public const string DatafileFilename = "datafile.json";

    public const string PreviewFilename = "preview.json";

    public const string StreamUnavailableReason =
        "Streaming is on, but there is no stream endpoint to connect to: the datafile carries no "
        + "\"streamEndpoint\" field and no StreamEndpoint option was set. Republish the "
        + "environment from the A vs B dashboard (Environments in the project sidebar) to pick up the "
        + "field, or set StreamEndpoint explicitly. Polling stays on in the meantime, so flag "
        + "changes still arrive, just on the polling interval instead of instantly.";

    /// <summary>Kind name to ingestion path. Keys match the fixtures and the other ports.</summary>
    public static readonly IReadOnlyDictionary<string, string> CollectPaths =
        new Dictionary<string, string>
        {
            ["single"] = CollectPathSingle,
            ["batch"] = CollectPathBatch,
            ["metricBatch"] = CollectPathMetricBatch,
            ["orders"] = CollectPathOrders,
            ["errors"] = CollectPathErrors,
            ["ping"] = CollectPathPing,
            ["geo"] = CollectPathGeo,
        };

    /// <summary>
    /// Canonical datafile URL: {cdnHost}/{sdkKey}/datafile.json.
    ///
    /// This is the ONLY correct shape. The prior "sdk.avsb.cloud/v1" base with a leading-slash
    /// Uri combine erased the /v1 segment and fetched /datafile/{key}, which 404s.
    /// </summary>
    /// <param name="cdnHost">CDN origin, or null for the public CDN.</param>
    public static string DatafileUrl(string sdkKey, string? cdnHost = null)
    {
        var key = RequireSdkKey(sdkKey, nameof(DatafileUrl));
        return $"{HostOrDefault(cdnHost, DefaultCdnHost)}/{key}/{DatafileFilename}";
    }

    /// <summary>Per-environment preview snapshot. Served with no-store.</summary>
    public static string PreviewUrl(string sdkKey, string? cdnHost = null)
    {
        var key = RequireSdkKey(sdkKey, nameof(PreviewUrl));
        return $"{HostOrDefault(cdnHost, DefaultCdnHost)}/{key}/{PreviewFilename}";
    }

    /// <summary>CDN object key for the live datafile.</summary>
    public static string DatafileObjectKey(string sdkKey) =>
        $"{RequireSdkKey(sdkKey, nameof(DatafileObjectKey))}/{DatafileFilename}";

    /// <summary>CDN object key for the preview snapshot.</summary>
    public static string PreviewObjectKey(string sdkKey) =>
        $"{RequireSdkKey(sdkKey, nameof(PreviewObjectKey))}/{PreviewFilename}";

    /// <summary>
    /// Normalise a datafile collectEndpoint value down to a bare origin.
    ///
    /// The flag datafile publishes an origin; the web-experiment datafile publishes the full
    /// batch URL. Both are valid inputs, so an SDK can never produce
    /// /v1/collect/batch/v1/collect/batch.
    /// </summary>
    public static string CollectOriginFrom(string? collectEndpoint)
    {
        var trimmed = TrimTrailingSlashes((collectEndpoint ?? string.Empty).Trim());
        foreach (var path in CollectPaths.Values)
        {
            if (trimmed.EndsWith(path, StringComparison.Ordinal))
            {
                return trimmed.Substring(0, trimmed.Length - path.Length);
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Build an ingestion URL. <paramref name="kind"/> is a key of <see cref="CollectPaths"/>;
    /// pass null for <paramref name="collectEndpoint"/> to use the public ingest host.
    /// </summary>
    public static string CollectUrl(string kind, string? collectEndpoint = null)
    {
        if (!CollectPaths.TryGetValue(kind, out var path))
        {
            var known = CollectPaths.Keys.OrderBy(k => k, StringComparer.Ordinal);
            throw new ArgumentException(
                $"CollectUrl got an unknown endpoint kind \"{kind}\". Known kinds: "
                + $"{string.Join(", ", known)}.",
                nameof(kind));
        }

        var origin = string.IsNullOrWhiteSpace(collectEndpoint)
            ? DefaultCollectHost
            : CollectOriginFrom(collectEndpoint);
        return origin + path;
    }

    /// <summary>
    /// Outcome of <see cref="ResolveStreamEndpoint"/>. When Available is false, Reason explains
    /// what to do and Endpoint is null: never guess a host.
    /// </summary>
    public sealed record StreamEndpointResolution
    {
        public bool Available { get; init; }

        public string? Endpoint { get; init; }

        /// <summary>"option" or "datafile" when available.</summary>
        public string? Source { get; init; }

        public string? Reason { get; init; }
    }

    /// <summary>
    /// Resolve the stream ticket endpoint. Precedence: explicit option, then the
    /// server-controlled datafile field, then nothing. A default of stream.avsb.cloud gave every
    /// Go customer an infinite reconnect loop against DNS that does not resolve; this returns
    /// unavailable instead.
    /// </summary>
    public static StreamEndpointResolution ResolveStreamEndpoint(
        string? optionEndpoint = null,
        string? datafileStreamEndpoint = null)
    {
        if (!string.IsNullOrWhiteSpace(optionEndpoint))
        {
            return new StreamEndpointResolution
            {
                Available = true,
                Endpoint = TrimTrailingSlashes(optionEndpoint!.Trim()),
                Source = "option",
            };
        }

        if (!string.IsNullOrWhiteSpace(datafileStreamEndpoint))
        {
            return new StreamEndpointResolution
            {
                Available = true,
                Endpoint = TrimTrailingSlashes(datafileStreamEndpoint!.Trim()),
                Source = "datafile",
            };
        }

        return new StreamEndpointResolution
        {
            Available = false,
            Reason = StreamUnavailableReason,
        };
    }

    /// <summary>Step 1 of streaming: the ticket request URL.</summary>
    public static string StreamTicketUrl(string streamEndpoint, string sdkKey)
    {
        var key = RequireSdkKey(sdkKey, nameof(StreamTicketUrl));
        return $"{TrimTrailingSlashes(streamEndpoint)}?sdkKey={Uri.EscapeDataString(key)}";
    }

    /// <summary>
    /// Step 2 of streaming: the SSE URL. <paramref name="subscribeBaseUrl"/> is the url field the
    /// TICKET RESPONSE handed back, not the ticket endpoint itself. The token rides in the query
    /// string because SSE clients cannot set request headers; tokens expire in 60 seconds.
    /// </summary>
    public static string StreamSubscribeUrl(string subscribeBaseUrl, string sdkKey, string token)
    {
        var key = RequireSdkKey(sdkKey, nameof(StreamSubscribeUrl));
        return $"{TrimTrailingSlashes(subscribeBaseUrl)}?sdkKey={Uri.EscapeDataString(key)}"
            + $"&token={Uri.EscapeDataString(token ?? string.Empty)}";
    }

    /// <summary>Fallback stream ticket URL, for docs and tests.</summary>
    public static string StreamEndpointUrl(string? appHost = null) =>
        HostOrDefault(appHost, DefaultAppHost) + StreamTicketPath;

    /// <summary>Fallback heartbeat URL, for docs and tests.</summary>
    public static string HeartbeatUrl(string? appHost = null) =>
        HostOrDefault(appHost, DefaultAppHost) + HeartbeatPath;

    /// <summary>
    /// Returns the datafile's heartbeat endpoint, or null meaning "do not send heartbeats".
    ///
    /// Deliberately not defaulted to app.avsb.cloud: a proxied deployment publishes its own
    /// origin, and heartbeats posted to the wrong host would report liveness nowhere while
    /// looking healthy.
    /// </summary>
    public static string? ResolveHeartbeatEndpoint(string? datafileHeartbeatEndpoint) =>
        string.IsNullOrWhiteSpace(datafileHeartbeatEndpoint)
            ? null
            : datafileHeartbeatEndpoint!.Trim();

    private static string HostOrDefault(string? host, string fallback) =>
        string.IsNullOrWhiteSpace(host) ? fallback : TrimTrailingSlashes(host!.Trim());

    private static string TrimTrailingSlashes(string host) =>
        (host ?? string.Empty).TrimEnd('/');

    /// <summary>
    /// Loud guard: an empty key builds https://cdn.avsb.cloud//datafile.json, which 404s at the
    /// edge with nothing in the message to explain why. Full key-shape validation belongs in the
    /// constructor (spec §1.2); this only catches "nothing was passed".
    /// </summary>
    private static string RequireSdkKey(string sdkKey, string fnName)
    {
        var trimmed = (sdkKey ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                $"{fnName} was called without an SDK key. Pass the environment SDK key (it looks "
                + "like \"sdk_production_xxxxxxxx\"), which you can copy from Settings, then "
                + "Environments, in the A vs B dashboard.",
                nameof(sdkKey));
        }

        return trimmed;
    }
}
