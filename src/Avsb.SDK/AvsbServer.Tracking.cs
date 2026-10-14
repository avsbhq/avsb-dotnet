using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Logger;
using Avsb.SDK.Tracking;

namespace Avsb.SDK;

// AvsbServer, continued (FU-31): conversions, purchases, the tracker, the decision log and liveness.
public sealed partial class AvsbServer
{
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
        IReadOnlyCollection<string> privateAttrs = ContextHelper.PrivateAttributesFrom(ctx);
        _decisionLog.Add(privateAttrs.Count > 0
            ? Avsb.SDK.DecisionLog.Redact(entry, privateAttrs)
            : entry);
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
}
