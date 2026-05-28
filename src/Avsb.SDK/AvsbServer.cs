using System.Diagnostics;
using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Logger;
using Avsb.SDK.Streaming;

namespace Avsb.SDK;

/// <summary>
/// Server-side AvsB SDK entry point. Thread-safe. Designed to be registered
/// as a singleton in DI containers.
///
/// Usage:
///   var server = new AvsbServer(new AvsbServerOptions { SdkKey = "..." });
///   await server.InitAsync();
///   var flag = server.GetBoolFlag("my-flag", false, EvalContext.User("alice"));
/// </summary>
public sealed class AvsbServer : IAsyncDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://sdk.avsb.cloud/v1");
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly AvsbServerOptions _options;
    private readonly AvsbLogger _logger;
    private readonly HttpClient _http;
    private readonly DecisionLog _decisionLog = new();
    private readonly CancellationTokenSource _cts = new();

    private volatile FlagDatafile? _datafile;
    private Task? _pollLoop;
    private SseClient? _sseClient;

    public AvsbServer(AvsbServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = new AvsbLogger(options.Logger);
        _http = new HttpClient();
    }

    /// <summary>
    /// Fetch the initial datafile and start the background polling loop.
    /// Returns an InitResult describing whether the fetch succeeded.
    /// </summary>
    public async Task<InitResult> InitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await FetchDatafileAsync(cancellationToken).ConfigureAwait(false);
            if (json is not null)
            {
                _datafile = FlagDatafile.Deserialize(json);
                _logger.Info("Datafile loaded successfully.");
                _pollLoop = PollLoopAsync(_cts.Token);
                return new InitResult { Success = true, Source = InitResultSource.Network };
            }

            _logger.Warn("Datafile fetch returned empty; SDK running on defaults.");
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Error,
                Degraded = true,
            };
        }
        catch (TaskCanceledException ex)
        {
            _logger.Warn("InitAsync timed out.");
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Timeout,
                Error = ex,
                Degraded = true,
            };
        }
        catch (Exception ex)
        {
            _logger.Error("InitAsync failed.", ex);
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Error,
                Error = ex,
                Degraded = true,
            };
        }
    }

    // -----------------------------------------------------------------------
    // Public evaluation surface.
    // -----------------------------------------------------------------------

    /// <summary>Evaluate a boolean flag.</summary>
    public Flag<bool> GetBoolFlag(string key, bool defaultValue, EvalContext? context = null) =>
        Evaluate<bool>(key, defaultValue, context);

    /// <summary>Evaluate a string flag.</summary>
    public Flag<string> GetStringFlag(string key, string defaultValue, EvalContext? context = null) =>
        Evaluate<string>(key, defaultValue, context);

    /// <summary>Evaluate a numeric flag.</summary>
    public Flag<double> GetNumberFlag(string key, double defaultValue, EvalContext? context = null) =>
        Evaluate<double>(key, defaultValue, context);

    /// <summary>Evaluate a JSON flag, deserialising to T.</summary>
    public Flag<T?> GetJsonFlag<T>(string key, T? defaultValue, EvalContext? context = null)
    {
        var raw = Evaluate<string?>(key, null, context);
        if (raw.Value is null)
        {
            return new Flag<T?>
            {
                Value = defaultValue,
                VariationKey = raw.VariationKey,
                Source = raw.Source,
                RuleId = raw.RuleId,
                RuleType = raw.RuleType,
                Reasons = raw.Reasons,
                EvaluatedAt = raw.EvaluatedAt,
                DurationMicros = raw.DurationMicros,
            };
        }
        try
        {
            var deserialized = JsonSerializer.Deserialize<T>(raw.Value);
            return new Flag<T?>
            {
                Value = deserialized,
                VariationKey = raw.VariationKey,
                Source = raw.Source,
                RuleId = raw.RuleId,
                RuleType = raw.RuleType,
                Reasons = raw.Reasons,
                EvaluatedAt = raw.EvaluatedAt,
                DurationMicros = raw.DurationMicros,
            };
        }
        catch
        {
            return new Flag<T?>
            {
                Value = defaultValue,
                VariationKey = null,
                Source = EvaluationSource.Default,
                RuleId = null,
                RuleType = null,
                Reasons = new[] { "JSON deserialization failed, returning default" },
                EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DurationMicros = 0,
            };
        }
    }

    /// <summary>Track a custom metric event (fire-and-forget).</summary>
    public void Track(string eventName, EvalContext? context = null, double? value = null,
        IDictionary<string, object?>? properties = null)
    {
        // Telemetry dispatch is an async concern; in a production implementation
        // this enqueues to an outgoing batch buffer. For now we log and return.
        _logger.Info($"Track: event={eventName} value={value}");
    }

    /// <summary>Identify a context (sends an identify event for analytics).</summary>
    public void Identify(EvalContext context)
    {
        _logger.Info($"Identify: context={context.Single?.Key ?? "(multi)"}");
    }

    // -----------------------------------------------------------------------
    // Decision log accessor.
    // -----------------------------------------------------------------------

    public IReadOnlyList<DecisionLogEntry> DecisionLogEntries => _decisionLog.Entries;

    // -----------------------------------------------------------------------
    // Internal evaluation helper.
    // -----------------------------------------------------------------------

    private Flag<T> Evaluate<T>(string key, T defaultValue, EvalContext? context)
    {
        var sw = Stopwatch.GetTimestamp();
        var datafile = _datafile;
        var reasons = new List<string>();

        if (datafile is null)
        {
            reasons.Add("SDK not initialised; returning default");
            return BuildFlag<T>(key, defaultValue, null, EvaluationSource.NotFound,
                null, null, reasons, sw);
        }

        var ctx = context ?? EvalContext.User(string.Empty);
        var result = Evaluator.EvaluateFlag(key, defaultValue, ctx, datafile, reasons);

        T typedValue;
        try { typedValue = CastValue<T>(result.Value, defaultValue); }
        catch { typedValue = defaultValue; }

        var flag = BuildFlag(key, typedValue, result.VariationKey, result.Source,
            result.RuleId, result.RuleType, result.Reasons, sw);

        // Sticky bucket: save when a rule matched.
        if (_options.StickyBucketService is not null && result.RuleId is not null
            && result.RuleType is not null)
        {
            var userId = ContextHelper.BucketingKey(ctx);
            if (!string.IsNullOrEmpty(userId))
            {
                _options.StickyBucketService.Save(userId, key, new StickyAssignment
                {
                    VariationId = result.VariationKey ?? string.Empty,
                    RuleId = result.RuleId,
                    RuleType = result.RuleType.Value,
                    AssignedAt = flag.EvaluatedAt,
                });
            }
        }

        // Log to decision log.
        var summary = ContextHelper.ContextSummary(ctx);
        var privateAttrs = ContextHelper.PrivateAttributesFrom(ctx);
        var entry = new DecisionLogEntry
        {
            FlagKey = key,
            Value = result.Value,
            VariationKey = result.VariationKey,
            Source = result.Source,
            RuleId = result.RuleId,
            RuleType = result.RuleType,
            Reasons = result.Reasons,
            EvaluatedAt = flag.EvaluatedAt,
            DurationMicros = flag.DurationMicros,
            ContextSummary = summary,
        };
        _decisionLog.Add(privateAttrs.Count > 0 ? DecisionLog.Redact(entry, privateAttrs) : entry);

        return flag;
    }

    private static Flag<T> BuildFlag<T>(
        string key, T value, string? variationKey,
        EvaluationSource source, string? ruleId, RuleType? ruleType,
        IReadOnlyList<string> reasons, long startTimestamp)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        long micros = (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency);
        return new Flag<T>
        {
            Value = value,
            VariationKey = variationKey,
            Source = source,
            RuleId = ruleId,
            RuleType = ruleType,
            Reasons = reasons,
            EvaluatedAt = nowMs,
            DurationMicros = Math.Max(0, micros),
        };
    }

    private static T CastValue<T>(object? value, T fallback)
    {
        if (value is null) return fallback;
        if (value is T typed) return typed;
        // Coerce via string round-trip for primitives.
        if (typeof(T) == typeof(bool) && value is string s)
            return (T)(object)(s.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (typeof(T) == typeof(double))
            return (T)(object)Convert.ToDouble(value,
                System.Globalization.CultureInfo.InvariantCulture);
        if (typeof(T) == typeof(string))
            return (T)(object)value.ToString()!;
        return fallback;
    }

    // -----------------------------------------------------------------------
    // Background polling.
    // -----------------------------------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = _options.PollInterval ?? DefaultPollInterval;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            try
            {
                var json = await FetchDatafileAsync(ct).ConfigureAwait(false);
                if (json is not null)
                    _datafile = FlagDatafile.Deserialize(json);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warn("Poll failed.", ex);
            }
        }
    }

    private async Task<string?> FetchDatafileAsync(CancellationToken ct)
    {
        var base_ = _options.Endpoint ?? DefaultEndpoint;
        var url = new Uri(base_, $"/datafile/{_options.SdkKey}");
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // IAsyncDisposable.
    // -----------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_sseClient is not null) await _sseClient.DisposeAsync().ConfigureAwait(false);
        if (_pollLoop is not null)
        {
            try { await _pollLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _http.Dispose();
        _cts.Dispose();
    }
}
