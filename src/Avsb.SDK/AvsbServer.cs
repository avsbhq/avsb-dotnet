using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Logger;
using Avsb.SDK.Tracking;

namespace Avsb.SDK;

/// <summary>
/// Server-side A vs B SDK. Thread-safe; register it as a singleton.
///
/// <code>
/// var server = new AvsbServer(new AvsbServerOptions { SdkKey = "sdk_production_xxxxxxxxxxxx" });
/// await server.InitAsync();
///
/// var flag = server.GetBoolFlag("checkout-v2", false, EvalContext.User("user_42"));
/// if (flag.IsEnabled()) { /* new checkout */ }
///
/// server.Track("signup_completed", EvalContext.User("user_42"));
/// await server.DisposeAsync();   // flushes queued events
/// </code>
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
public sealed class AvsbServer : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Plus or minus this fraction of the poll interval, so fleets do not fetch in lockstep.</summary>
    private const double PollJitter = 0.1;

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

    public AvsbServer(AvsbServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
    // Lifecycle.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetch the datafile and start the background polling loop.
    ///
    /// The polling loop starts EVEN WHEN THE FIRST FETCH FAILS. Previously it started only on
    /// first-fetch success, so any startup blip left the SDK serving defaults forever with no retry
    /// and no bootstrap fallback: a transient network error at deploy time bricked flags until the
    /// process restarted.
    /// </summary>
    public async Task<InitResult> InitAsync(CancellationToken cancellationToken = default)
    {
        InitResult result = await FetchAndApplyAsync(cancellationToken).ConfigureAwait(false);
        StartPollLoop();
        return result;
    }

    /// <summary>Load a datafile from JSON. Used for bootstrap and in tests.</summary>
    public void LoadDatafile(string json)
    {
        FlagDatafile parsed = FlagDatafile.Deserialize(json)
            ?? throw new ArgumentException("Datafile JSON did not parse.", nameof(json));
        _datafile = parsed;
        StartTrackerIfNeeded(parsed);
    }

    /// <summary>The datafile currently in effect, or null before one loads.</summary>
    public FlagDatafile? Datafile => _datafile;

    /// <summary>True once a datafile is loaded and flags can be read.</summary>
    public bool IsReady => _datafile is not null;

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
    // Tracking.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Record a conversion or metric event.
    ///
    /// Queued and sent in batches to the ingest endpoint the datafile publishes, retried with
    /// jittered backoff, and flushed on dispose. It used to log a line and drop the event, which is
    /// worse than a missing method: nothing told the caller their conversion data was being
    /// discarded.
    /// </summary>
    public void Track(string eventName, EvalContext? context = null, TrackPayload? payload = null)
    {
        ServerTracker? tracker = _tracker;
        if (tracker is null)
        {
            _logger.Warn($"Track(\"{eventName}\") was dropped because event sending is not running:"
                + " either the SDK has no datafile yet (await InitAsync first) or Tracking was set"
                + " to false in AvsbServerOptions.");
            return;
        }

        EvalContext? ctx = payload?.Context ?? context;
        tracker.QueueMetric(new MetricEvent
        {
            VisitorId = ctx is null ? string.Empty : ContextHelper.BucketingKey(ctx),
            EventName = eventName,
            Revenue = payload?.Revenue,
            Value = payload?.Value,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PageUrl = string.Empty,
        });
    }

    /// <summary>
    /// Record a purchase. Sent immediately rather than batched, because an order is the event a
    /// customer least wants buffered. Total is decimal major units as displayed (99.95, not 9995).
    /// </summary>
    public Task TrackPurchaseAsync(EvalContext? context, PurchaseOrder order)
    {
        ServerTracker? tracker = _tracker;
        if (tracker is null)
        {
            _logger.Warn($"TrackPurchaseAsync(\"{order.OrderId}\") was dropped because event sending"
                + " is not running: either the SDK has no datafile yet (await InitAsync first) or"
                + " Tracking was set to false in AvsbServerOptions.");
            return Task.CompletedTask;
        }
        return tracker.TrackPurchaseAsync(
            context is null ? string.Empty : ContextHelper.BucketingKey(context), order);
    }

    /// <summary>Send anything queued now, without waiting for the flush timer.</summary>
    public Task FlushEventsAsync() => _tracker?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>The decision log for this client. Bounded; see <see cref="Avsb.SDK.DecisionLog"/>.</summary>
    public DecisionLog DecisionLog => _decisionLog;

    /// <summary>Retained decision-log entries.</summary>
    public IReadOnlyList<DecisionLogEntry> DecisionLogEntries => _decisionLog.Entries;

    // -----------------------------------------------------------------------
    // Internal evaluation.
    // -----------------------------------------------------------------------

    private delegate bool Coercer<T>(object? raw, out T value, out string mismatch);

    private Flag<T> Evaluate<T>(
        string key, T defaultValue, EvalContext? context, string declaredType, Coercer<T> coerce)
    {
        long started = Stopwatch.GetTimestamp();
        FlagDatafile? datafile = _datafile;

        if (datafile is null)
        {
            string reason = _disposed
                ? $"AvsB SDK is disposed, so flag \"{key}\" cannot be evaluated. Build a new"
                    + " AvsbServer if you need flags again."
                : $"AvsB SDK is not ready: no datafile is loaded, so flag \"{key}\" fell back to"
                    + " your default. Await InitAsync (or set Bootstrap) before evaluating.";
            _logger.Warn(reason);
            return Flag<T>.NotReady(defaultValue, reason);
        }

        EvalContext ctx = context ?? EvalContext.User(string.Empty);
        WarnOnEmptyContextOnce(ctx);

        var reasons = new List<string>();
        Evaluator.EvalResult result = EvaluateRaw(datafile, key, defaultValue, ctx, reasons);
        RecordDecision(key, ctx, result, started);

        // The datafile's declared type wins over guessing: asking for a bool on a string flag is a
        // configuration mistake, and saying so beats returning a coerced value.
        FlagDatafileEntry? entry = datafile.Flags.Find(f => f.Key == key);
        if (entry is not null && !string.IsNullOrEmpty(entry.Type) && entry.Type != declaredType)
        {
            string reason = $"flag \"{key}\" is declared as \"{entry.Type}\" in the datafile but a"
                + $" \"{declaredType}\" value was requested; returning your default. Use"
                + $" {GetterName(entry.Type)} instead.";
            _logger.Warn(reason);
            return Flag<T>.NotFound(defaultValue, reason);
        }

        if (!coerce(result.Value, out T typed, out string mismatch))
        {
            string reason = $"flag \"{key}\" {mismatch}; returning your default.";
            _logger.Warn(reason);
            return Flag<T>.NotFound(defaultValue, reason);
        }

        return BuildFlag(typed, result, started);
    }

    /// <summary>The three client-layer steps plus the evaluator, with no type coercion.</summary>
    private Evaluator.EvalResult EvaluateRaw(
        FlagDatafile datafile, string key, object? defaultValue, EvalContext ctx,
        List<string> reasons)
    {
        // 1. Runtime override, per flag key, resolved against the datafile's variations.
        Evaluator.EvalResult? runtime = ResolveRuntimeOverride(datafile, key, reasons);
        if (runtime is not null) return runtime.Value;

        // 2. Sticky lookup, before the evaluator, so a stored assignment beats a rule and even
        //    beats a datafile override (the node pattern, locked by the corpus).
        Evaluator.EvalResult? sticky = ResolveSticky(datafile, key, ctx, reasons);
        if (sticky is not null) return sticky.Value;

        // 3. The evaluator. The caller's default goes in, so a datafile that cannot answer hands
        //    back the caller's own value with the real source rather than something invented.
        var options = new EvaluateOptions
        {
            OnExposure = _tracker is null ? null : _tracker.Queue,
            BanditModelStore = _options.BanditModelStore,
        };
        Evaluator.EvalResult result =
            Evaluator.EvaluateFlag(key, defaultValue, ctx, datafile, reasons, options);
        PersistStickyAssignment(datafile, key, ctx, result);
        return result;
    }

    /// <summary>
    /// A runtime override is a flag key mapped to a VARIATION KEY, resolved against the datafile so
    /// the served value and its key are real. A key that names no variation is logged and ignored
    /// rather than serving something invented.
    /// </summary>
    private Evaluator.EvalResult? ResolveRuntimeOverride(
        FlagDatafile datafile, string flagKey, List<string> reasons)
    {
        if (_options.RuntimeOverrides is null
            || !_options.RuntimeOverrides.TryGetValue(flagKey, out string? variationKey))
        {
            return null;
        }

        FlagDatafileEntry? flag = datafile.Flags.Find(f => f.Key == flagKey);
        if (flag is null)
        {
            _logger.Warn($"Runtime override for flag \"{flagKey}\" was ignored: that flag is not in"
                + " the datafile for this environment.");
            return null;
        }
        FlagDatafileVariation? variation = flag.Variations.Find(v => v.Key == variationKey);
        if (variation is null)
        {
            _logger.Warn($"Runtime override for flag \"{flagKey}\" was ignored: variation"
                + $" \"{variationKey}\" is not one of that flag's variations"
                + $" ({string.Join(", ", flag.Variations.Select(v => v.Key))}).");
            return null;
        }

        reasons.Add($"matched runtime override for flagKey=\"{flagKey}\"");
        return new Evaluator.EvalResult
        {
            Value = variation.Value,
            VariationKey = variation.Key,
            Source = EvaluationSource.RuntimeOverride,
            RuleId = null,
            RuleType = null,
            Reasons = reasons,
        };
    }

    /// <summary>
    /// Sticky lookup. Runs BEFORE the evaluator, so a stored assignment wins over rules and over a
    /// datafile override: a user who was already bucketed stays bucketed. Nothing ever called
    /// Lookup before this.
    ///
    /// A stored variation that no longer exists is a MISS, not an error. A stored variation whose
    /// rule is gone is still served, with a warning and no exposure, because the rule it would be
    /// attributed to no longer exists.
    /// </summary>
    private Evaluator.EvalResult? ResolveSticky(
        FlagDatafile datafile, string flagKey, EvalContext ctx, List<string> reasons)
    {
        if (_options.StickyBucketService is null) return null;

        string userId = ContextHelper.BucketingKey(ctx);
        if (string.IsNullOrEmpty(userId)) return null;

        StickyAssignment? assignment = _options.StickyBucketService.Lookup(userId, flagKey);
        if (assignment is null) return null;

        FlagDatafileEntry? flag = datafile.Flags.Find(f => f.Key == flagKey);
        FlagDatafileVariation? stored = flag?.Variations.Find(v => v.Id == assignment.VariationId);
        if (flag is null || stored is null) return null;

        bool ruleStillPresent = flag.Rules
            .Where(r => r.Id == assignment.RuleId)
            .Any(r => r.Variations.Any(rv => rv.VariationId == stored.Id));
        if (!ruleStillPresent)
        {
            _logger.Warn($"Sticky assignment for flag \"{flagKey}\" references a rule that no longer"
                + " serves that variation, so the stored value is being served without an exposure"
                + $" event (userId={userId}, ruleId={assignment.RuleId}).");
        }

        reasons.Add($"matched sticky bucket assignment for userId=\"{userId}\"");
        return new Evaluator.EvalResult
        {
            Value = stored.Value,
            VariationKey = stored.Key,
            Source = EvaluationSource.Sticky,
            RuleId = assignment.RuleId,
            RuleType = assignment.RuleType,
            Reasons = reasons,
        };
    }

    /// <summary>
    /// Persist a sticky assignment. ONLY for an ab_test rule that served a variation, and the
    /// stored field is the variation's ID.
    ///
    /// Both halves were wrong before: it saved on every rule type including holdouts, and it saved
    /// the variation KEY into VariationId. Lookup resolves by id, so the first real use produced a
    /// corrupted store and no sticky behaviour at all.
    /// </summary>
    private void PersistStickyAssignment(
        FlagDatafile datafile, string flagKey, EvalContext ctx, Evaluator.EvalResult result)
    {
        if (_options.StickyBucketService is null) return;
        if (result.Source != EvaluationSource.Rule
            || result.RuleType != SDK.RuleType.AbTest
            || result.RuleId is null
            || result.VariationKey is null)
        {
            return;
        }

        string userId = ContextHelper.BucketingKey(ctx);
        if (string.IsNullOrEmpty(userId)) return;

        FlagDatafileEntry? flag = datafile.Flags.Find(f => f.Key == flagKey);
        FlagDatafileVariation? variation = flag?.Variations.Find(v => v.Key == result.VariationKey);
        if (variation is null) return;

        _options.StickyBucketService.Save(userId, flagKey, new StickyAssignment
        {
            VariationId = variation.Id,
            RuleId = result.RuleId,
            RuleType = result.RuleType.Value,
            AssignedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private void RecordDecision(
        string key, EvalContext ctx, Evaluator.EvalResult result, long startTimestamp)
    {
        if (_decisionLog.Capacity == 0) return;

        var entry = new DecisionLogEntry
        {
            FlagKey = key,
            Value = result.Value is JsonElement element
                ? Evaluator.UnwrapJson(element)
                : result.Value,
            VariationKey = result.VariationKey,
            Source = result.Source,
            RuleId = result.RuleId,
            RuleType = result.RuleType,
            Reasons = result.Reasons,
            EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DurationMicros = ElapsedMicros(startTimestamp),
            ContextSummary = ContextHelper.ContextSummary(ctx),
        };
        IReadOnlySet<string> privateAttrs = ContextHelper.PrivateAttributesFrom(ctx);
        _decisionLog.Add(privateAttrs.Count > 0
            ? Avsb.SDK.DecisionLog.Redact(entry, privateAttrs)
            : entry);
    }

    /// <summary>
    /// Warn once per client about an empty bucketing key. Every caller who forgets the context
    /// lands the whole fleet in one bucket, which corrupts an experiment invisibly, so the message
    /// names the exact call to add.
    /// </summary>
    private void WarnOnEmptyContextOnce(EvalContext ctx)
    {
        if (!string.IsNullOrEmpty(ContextHelper.BucketingKey(ctx))) return;
        if (Interlocked.Exchange(ref _emptyContextWarned, 1) != 0) return;
        _logger.Warn("AvsB evaluated a flag with an empty bucketing key, so every request will land"
            + " in the same bucket and experiment results will be wrong. Pass a real identity:"
            + " EvalContext.User(\"<your user id>\").");
    }

    // -----------------------------------------------------------------------
    // Type coercion.
    // -----------------------------------------------------------------------

    private static bool TryCoerceBool(object? raw, out bool value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                value = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                value = false;
                return true;
            default:
                value = false;
                mismatch = $"resolved to {DescribeValue(raw)}, not a boolean";
                return false;
        }
    }

    private static bool TryCoerceString(object? raw, out string value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case string s:
                value = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                value = element.GetString() ?? string.Empty;
                return true;
            default:
                value = string.Empty;
                mismatch = $"resolved to {DescribeValue(raw)}, not a string";
                return false;
        }
    }

    private static bool TryCoerceDouble(object? raw, out double value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case float f:
                value = f;
                return true;
            case decimal dec:
                value = (double)dec;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                value = element.GetDouble();
                return true;
            default:
                value = 0;
                mismatch = $"resolved to {DescribeValue(raw)}, not a number";
                return false;
        }
    }

    private static bool TryCoerceJson<T>(object? raw, out T? value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case null:
                value = default;
                return true;
            case T typed:
                value = typed;
                return true;
            case JsonElement element:
                try
                {
                    value = element.Deserialize<T>(JsonOptions.Default);
                    return true;
                }
                catch (JsonException ex)
                {
                    value = default;
                    mismatch = "resolved to a JSON document that does not map to "
                        + typeof(T).Name + " (" + ex.Message + ")";
                    return false;
                }
            default:
                value = default;
                mismatch = $"resolved to {DescribeValue(raw)}, which does not map to {typeof(T).Name}";
                return false;
        }
    }

    private static string DescribeValue(object? raw) => raw switch
    {
        null => "null",
        bool => "a boolean",
        string => "a string",
        double or int or long or float or decimal => "a number",
        JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } => "a boolean",
        JsonElement { ValueKind: JsonValueKind.String } => "a string",
        JsonElement { ValueKind: JsonValueKind.Number } => "a number",
        JsonElement { ValueKind: JsonValueKind.Array } => "a JSON array",
        JsonElement { ValueKind: JsonValueKind.Object } => "a JSON object",
        JsonElement { ValueKind: JsonValueKind.Null } => "null",
        _ => "a " + raw.GetType().Name,
    };

    private static string GetterName(string declaredType) => declaredType switch
    {
        "boolean" => "GetBoolFlag",
        "string" => "GetStringFlag",
        "number" => "GetNumberFlag",
        "json" => "GetJsonFlag",
        _ => "GetJsonFlag",
    };

    private static Flag<T> BuildFlag<T>(T value, Evaluator.EvalResult result, long startTimestamp) =>
        new()
        {
            Value = value,
            VariationKey = result.VariationKey,
            Source = result.Source,
            RuleId = result.RuleId,
            RuleType = result.RuleType,
            Reasons = result.Reasons,
            EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DurationMicros = ElapsedMicros(startTimestamp),
        };

    private static long ElapsedMicros(long startTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return Math.Max(0, (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency));
    }

    // -----------------------------------------------------------------------
    // Datafile transport.
    // -----------------------------------------------------------------------

    private async Task<InitResult> FetchAndApplyAsync(CancellationToken ct)
    {
        bool hadDatafile = _datafile is not null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _datafileUrl);
            // Datafile reads are keyless by path: the SDK key is the path segment. No
            // Authorization header, no X-SDK-Key, nothing to leak in a proxy log.
            request.Headers.TryAddWithoutValidation("User-Agent", SdkMetadata.UserAgent);

            using HttpResponseMessage response =
                await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error(FetchFailureMessage(
                    $"HTTP {(int)response.StatusCode}", (int)response.StatusCode));
                return new InitResult
                {
                    Success = false,
                    Source = InitResultSource.Error,
                    Degraded = hadDatafile,
                };
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            FlagDatafile? parsed = FlagDatafile.Deserialize(json);
            if (parsed is null)
            {
                _logger.Error(FetchFailureMessage(
                    "the response body did not parse as a datafile", 0));
                return new InitResult
                {
                    Success = false,
                    Source = InitResultSource.Error,
                    Degraded = hadDatafile,
                };
            }

            _datafile = parsed;
            _logger.Info($"Datafile loaded (version {parsed.Version}, {parsed.Flags.Count} flags).");
            StartTrackerIfNeeded(parsed);
            SendHeartbeat(parsed);
            return new InitResult { Success = true, Source = InitResultSource.Network };
        }
        catch (TaskCanceledException ex)
        {
            _logger.Error(FetchFailureMessage("the request timed out", 0), ex);
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Timeout,
                Error = ex,
                Degraded = hadDatafile,
            };
        }
        catch (Exception ex)
        {
            _logger.Error(FetchFailureMessage(ex.Message, 0), ex);
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Error,
                Error = ex,
                Degraded = hadDatafile,
            };
        }
    }

    /// <summary>
    /// A datafile failure names the URL tried, what came back, and hands over a curl that
    /// reproduces it. Three strings is the difference between "flags do not work" and a fix.
    /// </summary>
    private string FetchFailureMessage(string what, int status)
    {
        var message = new StringBuilder();
        message.Append("AvsB datafile fetch failed: ").Append(what)
            .Append(" for ").Append(_datafileUrl).Append(". ");
        if (status == 404)
        {
            message.Append("A 404 means the key is not published in this environment: check the SDK"
                + " key against Environments in the project sidebar, in the A vs B dashboard, and publish"
                + " the environment if it has never been published. ");
        }
        else if (status == 403)
        {
            message.Append("A 403 usually means a proxy or firewall in front of cdn.avsb.cloud is"
                + " blocking the request. ");
        }
        else if (status == 0)
        {
            message.Append("No HTTP status came back, so this is a network, DNS, TLS, or timeout"
                + " problem rather than a rejected request. ");
        }
        message.Append("Reproduce it with: curl -i ").Append(_datafileUrl);
        message.Append(" . Until a datafile loads, every flag returns the default you passed and"
            + " reports source not_ready.");
        return message.ToString();
    }

    private void StartPollLoop()
    {
        if (!_options.Polling || _disposed || _pollLoop is not null) return;
        _pollLoop = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        TimeSpan interval = _options.PollInterval ?? DefaultPollInterval;
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Jitter each cycle so a fleet that booted together does not stampede the CDN on
                // the same second forever.
                double jitterFactor = 1 + PollJitter * (Random.Shared.NextDouble() * 2 - 1);
                await Task.Delay(interval * jitterFactor, ct).ConfigureAwait(false);
                await FetchAndApplyAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A refresh failure must never kill the loop: the next cycle is the retry.
                _logger.Warn("Datafile refresh failed.", ex);
            }
        }
    }

    private void StartTrackerIfNeeded(FlagDatafile datafile)
    {
        if (!_options.Tracking || _disposed || _tracker is not null) return;
        _tracker = ServerTracker.Start(new ServerTracker.Options
        {
            SdkKey = _options.SdkKey,
            CollectEndpoint = datafile.CollectEndpoint,
            Logger = _logger,
        });
    }

    /// <summary>
    /// Report liveness after a successful datafile load. This is what turns an environment from
    /// "Not connected" into "Connected" on the dashboard, so an SDK that never sends one looks
    /// offline no matter how well it works.
    ///
    /// Fire and forget: a heartbeat is diagnostics and must never delay or fail an init. When the
    /// datafile carries no heartbeat endpoint, nothing is sent: a proxied deployment publishes its
    /// own origin, and posting to the public app host would report liveness nowhere while looking
    /// healthy.
    /// </summary>
    private void SendHeartbeat(FlagDatafile datafile)
    {
        string? endpoint = Endpoints.ResolveHeartbeatEndpoint(datafile.HeartbeatEndpoint);
        if (endpoint is null) return;

        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["sdkKey"] = _options.SdkKey,
            ["sdkType"] = SdkMetadata.SdkType,
            ["sdkVersion"] = SdkMetadata.SdkVersion,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response =
                    await _http.PostAsync(endpoint, content, _cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Debug($"Heartbeat returned HTTP {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Heartbeat failed.", ex);
            }
        });
    }

    // -----------------------------------------------------------------------
    // IAsyncDisposable.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stop the polling loop and flush queued events. After this, evaluation reports NotReady
    /// rather than serving a stale datafile.
    ///
    /// There is no _sseClient here any more. It was a field that was declared, disposed, and never
    /// assigned, so the SDK carried a streaming story it did not have. Streaming lands when the
    /// datafile's streamEndpoint field is wired; polling keeps flags fresh in the meantime.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
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
