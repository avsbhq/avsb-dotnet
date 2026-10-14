using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Logger;
using Avsb.SDK.Tracking;

namespace Avsb.SDK;

// One class across four files (FU-31): this file holds the fields, the constructor, the public
// flag getters and disposal. AvsbServer.Datafile.cs, AvsbServer.Evaluation.cs and
// AvsbServer.Tracking.cs hold the rest. Every field stays here, so field initialisation order
// does not depend on the order the compiler reads the partial files.

/// <summary>
/// Server-side A vs B SDK. Thread-safe; register it as a singleton.
///
/// <code>
/// var server = new AvsbServer(new AvsbServerOptions { SdkKey = "sdk_production_xxxxxxxxxxxx" });
/// await server.InitAsync();
///
/// var flag = server.GetBoolFlag("checkout_v2", false, EvalContext.User("user_42"));
/// if (flag.IsEnabled()) { /* new checkout */ }
///
/// server.Track("signup_completed", EvalContext.User("user_42"));
/// await server.DisposeAsync();   // flushes queued events
/// </code>
///
/// It is both <see cref="IAsyncDisposable"/> and <see cref="IDisposable"/>, so <c>using var</c>,
/// <c>await using var</c> and a container that disposes synchronously all work. Prefer
/// <see cref="DisposeAsync"/> where you can await: <see cref="Dispose"/> waits a bounded time for
/// the final event flush.
///
/// Full evaluation pipeline, in order. The first three steps live here, at the client layer,
/// exactly as @avsbhq/node arranges them; the rest are the pure evaluator: runtime override,
/// sticky lookup, datafile user override, holdout, rules (including bandit dispatch), flag default
/// variation.
///
/// Before a datafile exists, every getter returns YOUR default value with source
/// <see cref="EvaluationSource.NotReady"/> and a reason naming what to call. It is not NotFound:
/// "the flag does not exist" and "the SDK never started" are different problems and used to be
/// reported identically.
/// </summary>
public sealed partial class AvsbServer : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Plus or minus this fraction of the poll interval, so fleets do not fetch in lockstep.</summary>
    private const double PollJitter = 0.1;

    /// <summary>How long a synchronous <see cref="Dispose"/> waits for the final event flush.</summary>
    private static readonly TimeSpan SyncDisposeWait = TimeSpan.FromSeconds(5);

    private readonly AvsbServerOptions _options;
    private readonly AvsbLogger _logger;
    private readonly HttpClient _http;
    private readonly DecisionLog _decisionLog;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _datafileUrl;

    private volatile FlagDatafile? _datafile;
    private volatile ServerTracker? _tracker;
    private Task? _pollLoop;
    private int _emptyContextWarned;
    private volatile bool _disposed;
    private readonly object _disposeLock = new();
    private Task? _shutdown;

    public AvsbServer(AvsbServerOptions options)
    {
        // A copy, so a caller that keeps and mutates its options object cannot change a running
        // server underneath it (the options are settable for the ASP.NET Core pattern).
        _options = (options ?? throw new ArgumentNullException(nameof(options))) with { };
        _logger = new AvsbLogger(options.Logger);
        _http = new HttpClient { Timeout = options.FetchTimeout ?? DefaultFetchTimeout };
        _decisionLog = new DecisionLog(options.DecisionLogCapacity);

        // The datafile URL comes from the endpoint contract, never from a Uri combine. The old
        // form, new Uri(base, "/datafile/{key}"), discarded the base path because the relative part
        // started with a slash, so even a correctly configured "https://host/v1" endpoint fetched
        // "https://host/datafile/{key}" and 404d.
        _datafileUrl = !string.IsNullOrWhiteSpace(options.DatafileUrl)
            ? options.DatafileUrl!
            : Endpoints.DatafileUrl(options.SdkKey, options.CdnHost);

        SdkKeyShape.Result keyShape = SdkKeyShape.Validate(options.SdkKey);
        if (!keyShape.Valid)
        {
            // Loud, then proceed: an unrecognised key may still be valid, and the fetch is the real
            // arbiter.
            _logger.Error(keyShape.Message!);
        }

        if (!string.IsNullOrWhiteSpace(options.Bootstrap))
        {
            try
            {
                _datafile = FlagDatafile.Deserialize(options.Bootstrap!);
                if (_datafile is not null) StartTrackerIfNeeded(_datafile);
            }
            catch (JsonException ex)
            {
                _logger.Error("Bootstrap datafile is not valid JSON; ignoring it.", ex);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Public evaluation surface.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluate a boolean flag. A non-boolean value returns <paramref name="defaultValue"/> with
    /// source NotFound and a reason, never a coerced true.
    /// </summary>
    public Flag<bool> GetBoolFlag(string key, bool defaultValue, EvalContext? context = null) =>
        Evaluate(key, defaultValue, context, "boolean", TryCoerceBool);

    /// <summary>Evaluate a string flag. A number or boolean is a mismatch, not something to stringify.</summary>
    public Flag<string> GetStringFlag(string key, string defaultValue, EvalContext? context = null) =>
        Evaluate(key, defaultValue, context, "string", TryCoerceString);

    /// <summary>
    /// Evaluate a numeric flag. Every number flag resolves to a double: the reference engine has a
    /// single numeric type and cannot distinguish 3 from 3.0.
    /// </summary>
    public Flag<double> GetNumberFlag(string key, double defaultValue, EvalContext? context = null) =>
        Evaluate(key, defaultValue, context, "number", TryCoerceDouble);

    /// <summary>
    /// Evaluate a JSON flag, deserialising into T.
    ///
    /// This evaluates DIRECTLY. It used to read the flag as a string first and then parse that
    /// string, so a JSON flag whose value was a real JSON object resolved to null (the value is not
    /// a string) and every caller silently got their default.
    /// </summary>
    public Flag<T?> GetJsonFlag<T>(string key, T? defaultValue, EvalContext? context = null) =>
        Evaluate<T?>(key, defaultValue, context, "json", TryCoerceJson<T>);

    /// <summary>
    /// Evaluate every flag in the datafile: flag key to the raw JSON value, which is what a
    /// server-rendered page needs to bootstrap a browser client. Exposures fire for each decided
    /// flag, exactly as if each had been read individually.
    /// </summary>
    public IReadOnlyDictionary<string, object?> GetAllFlags(EvalContext? context = null)
    {
        FlagDatafile? datafile = _datafile;
        if (datafile is null)
        {
            _logger.Warn("AvsB SDK is not ready: GetAllFlags returned an empty map because no"
                + " datafile is loaded. Await InitAsync first.");
            return new Dictionary<string, object?>();
        }

        EvalContext ctx = context ?? EvalContext.User(string.Empty);
        WarnOnEmptyContextOnce(ctx);
        var output = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (FlagDatafileEntry flag in datafile.Flags)
        {
            long started = Stopwatch.GetTimestamp();
            var reasons = new List<string>();
            Evaluator.EvalResult result = EvaluateRaw(datafile, flag.Key, null, ctx, reasons);
            RecordDecision(flag.Key, ctx, result, started);
            output[flag.Key] = result.Value is JsonElement element
                ? Evaluator.UnwrapJson(element)
                : result.Value;
        }
        return output;
    }

    // -----------------------------------------------------------------------
    // IAsyncDisposable and IDisposable.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stop the polling loop and flush queued events. After this, evaluation reports NotReady
    /// rather than serving a stale datafile.
    ///
    /// Idempotent, and shares one shutdown with <see cref="Dispose"/>: whichever is called first
    /// starts it, and a later call of either kind joins it instead of running it twice.
    ///
    /// There is no _sseClient here any more. It was a field that was declared, disposed, and never
    /// assigned, so the SDK carried a streaming story it did not have. Streaming lands when the
    /// datafile's streamEndpoint field is wired; polling keeps flags fresh in the meantime.
    /// </summary>
    public ValueTask DisposeAsync() => new(StartShutdown());

    /// <summary>
    /// Synchronous disposal, for <c>using var</c> and for a dependency injection container that is
    /// disposed with <c>Dispose()</c> (which throws for a singleton that is only
    /// <see cref="IAsyncDisposable"/>). Runs the same shutdown as <see cref="DisposeAsync"/>,
    /// including the final flush, and waits for it up to five seconds. If the flush is still
    /// running then (an ingest endpoint that is slow to answer), it says so and lets the flush
    /// finish in the background rather than holding up the caller's shutdown.
    /// </summary>
    public void Dispose()
    {
        Task shutdown = StartShutdown();
        try
        {
            // Every await on the shutdown path uses ConfigureAwait(false), so blocking here cannot
            // deadlock on a synchronization context.
            if (!shutdown.Wait(SyncDisposeWait))
            {
                _logger.Warn(
                    $"Dispose waited {SyncDisposeWait.TotalSeconds:0} seconds for the final event flush and returned;"
                    + " the flush continues in the background. Await DisposeAsync to wait for it.");
            }
        }
        catch (AggregateException ex)
        {
            _logger.Warn("Shutdown failed.", ex.InnerException ?? ex);
        }
    }

    private Task StartShutdown()
    {
        lock (_disposeLock)
        {
            return _shutdown ??= ShutdownAsync();
        }
    }

    private async Task ShutdownAsync()
    {
        _disposed = true;
        _cts.Cancel();

        if (_pollLoop is not null)
        {
            try { await _pollLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }

        ServerTracker? tracker = _tracker;
        _tracker = null;
        if (tracker is not null)
        {
            await tracker.DisposeAsync().ConfigureAwait(false);
        }

        _datafile = null;
        _http.Dispose();
        _cts.Dispose();
    }
}
