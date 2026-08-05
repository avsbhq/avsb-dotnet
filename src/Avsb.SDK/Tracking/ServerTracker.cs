using System.Text;
using System.Text.Json;
using Avsb.SDK.Logger;

namespace Avsb.SDK.Tracking;

/// <summary>
/// Batched event sender. Mirrors createServerTracker in @avsbhq/node, so every server SDK batches,
/// retries, and flushes on the same terms.
///
/// What it guarantees:
///
/// - Bounded memory. The queues hold at most MaxQueueSize events each. Past that the OLDEST are
///   dropped with a warning naming the count, so a broken ingest endpoint cannot grow the heap
///   without limit or fail silently.
/// - Stable event ids. A GUID is stamped once, at enqueue time. Every retry of a batch carries the
///   same ids, so ingestion deduplicates instead of double counting a conversion.
/// - Jittered retries on the shared <see cref="Backoff"/> curve, honouring Retry-After.
/// - Flush on dispose, so a graceful shutdown does not throw away the last batch.
///
/// The JSON bodies are built field by field rather than reflected off the record types. The field
/// names are frozen vocabulary that the ingestion worker validates and the dashboard reads, and
/// optional fields must be OMITTED rather than sent as null (the ingestion schema treats them as
/// optional, not nullable, so a null fails the whole batch), so the wire shape is spelled out here
/// where it can be reviewed against the wire-shape conformance fixtures.
/// </summary>
public sealed class ServerTracker : IAsyncDisposable
{
    /// <summary>Ingestion accepts at most 100 items per batch.</summary>
    public const int MaxBatchSize = 100;

    public sealed record Options
    {
        /// <summary>
        /// Goes into every body envelope: the ingest routes are not key-pathed, so this is how an
        /// event is attributed to an environment.
        /// </summary>
        public string SdkKey { get; init; } = string.Empty;

        /// <summary>The datafile's collectEndpoint, or null for the public ingest host.</summary>
        public string? CollectEndpoint { get; init; }

        public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(10);
        public int BatchSize { get; init; } = 50;
        public int MaxQueueSize { get; init; } = 10_000;
        public int RetryAttempts { get; init; } = 3;
        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public AvsbLogger Logger { get; init; } = NullLogger.Instance;
    }

    private readonly Options _options;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _queueLock = new();
    private readonly List<ExposureEvent> _exposures = new();
    private readonly List<MetricEvent> _metrics = new();
    private readonly string _batchUrl;
    private readonly string _metricUrl;
    private readonly string _ordersUrl;
    private Task? _timer;
    private bool _closed;

    private ServerTracker(Options options)
    {
        _options = options;
        _http = new HttpClient { Timeout = options.RequestTimeout };
        _batchUrl = Endpoints.CollectUrl("batch", options.CollectEndpoint);
        _metricUrl = Endpoints.CollectUrl("metricBatch", options.CollectEndpoint);
        _ordersUrl = Endpoints.CollectUrl("orders", options.CollectEndpoint);
    }

    /// <summary>Create a tracker and start its flush timer.</summary>
    public static ServerTracker Start(Options options)
    {
        var tracker = new ServerTracker(options);
        tracker._timer = tracker.TimerLoopAsync(tracker._cts.Token);
        return tracker;
    }

    /// <summary>Queue an exposure. Stamps an event id if the event does not carry one.</summary>
    public void Queue(ExposureEvent evt)
    {
        if (_closed) return;
        ExposureEvent stamped = string.IsNullOrEmpty(evt.EventId)
            ? evt with { EventId = Guid.NewGuid().ToString() }
            : evt;

        bool shouldFlush;
        lock (_queueLock)
        {
            _exposures.Add(stamped);
            DropOldest(_exposures, "Event");
            shouldFlush = _exposures.Count >= EffectiveBatchSize;
        }
        if (shouldFlush) _ = FlushSafelyAsync();
    }

    /// <summary>Queue a conversion or metric event. Stamps an event id if absent.</summary>
    public void QueueMetric(MetricEvent evt)
    {
        if (_closed) return;
        MetricEvent stamped = string.IsNullOrEmpty(evt.EventId)
            ? evt with { EventId = Guid.NewGuid().ToString() }
            : evt;

        bool shouldFlush;
        lock (_queueLock)
        {
            _metrics.Add(stamped);
            DropOldest(_metrics, "Metric");
            shouldFlush = _metrics.Count >= EffectiveBatchSize;
        }
        if (shouldFlush) _ = FlushSafelyAsync();
    }

    /// <summary>
    /// Send a purchase immediately rather than on the batch timer, because an order is the event a
    /// customer least wants buffered.
    /// </summary>
    public async Task TrackPurchaseAsync(string visitorId, PurchaseOrder order)
    {
        if (_closed) return;
        string body = OrdersBody(visitorId, order, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await PostAsync(_ordersUrl, body, 1, $"order {order.OrderId}").ConfigureAwait(false);
    }

    /// <summary>Send everything queued now.</summary>
    public async Task FlushAsync()
    {
        List<ExposureEvent> exposures;
        List<MetricEvent> metrics;
        lock (_queueLock)
        {
            if (_exposures.Count == 0 && _metrics.Count == 0) return;
            exposures = new List<ExposureEvent>(_exposures);
            metrics = new List<MetricEvent>(_metrics);
            _exposures.Clear();
            _metrics.Clear();
        }

        // One send at a time: two concurrent flushes against the same endpoint gain nothing and
        // multiply the retry storm when it is down.
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            int chunk = EffectiveBatchSize;
            for (int i = 0; i < exposures.Count; i += chunk)
            {
                List<ExposureEvent> slice = exposures.GetRange(i, Math.Min(chunk, exposures.Count - i));
                await PostAsync(_batchUrl, ExposureBatchBody(slice), slice.Count, "exposures")
                    .ConfigureAwait(false);
            }
            for (int i = 0; i < metrics.Count; i += chunk)
            {
                List<MetricEvent> slice = metrics.GetRange(i, Math.Min(chunk, metrics.Count - i));
                await PostAsync(_metricUrl, MetricBatchBody(slice), slice.Count, "metric events")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Stop the timer and flush what is queued. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        _cts.Cancel();
        if (_timer is not null)
        {
            try { await _timer.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
        await FlushAsync().ConfigureAwait(false);
        _http.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    /// <summary>Queued exposure count. For tests and diagnostics.</summary>
    public int QueuedExposures
    {
        get { lock (_queueLock) return _exposures.Count; }
    }

    /// <summary>Queued metric-event count. For tests and diagnostics.</summary>
    public int QueuedMetricEvents
    {
        get { lock (_queueLock) return _metrics.Count; }
    }

    /// <summary>Queued exposures, in order. Internal: the enqueue-time id stamp is asserted.</summary>
    internal IReadOnlyList<ExposureEvent> SnapshotExposures()
    {
        lock (_queueLock) return _exposures.ToArray();
    }

    // -----------------------------------------------------------------------
    // Wire bodies
    // -----------------------------------------------------------------------

    /// <summary>{sdkKey, events: [...]} exactly as the batch fixture declares it.</summary>
    internal string ExposureBatchBody(IReadOnlyList<ExposureEvent> events)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sdkKey", _options.SdkKey);
            writer.WriteStartArray("events");
            foreach (ExposureEvent evt in events)
            {
                writer.WriteStartObject();
                writer.WriteString("eventId", evt.EventId ?? Guid.NewGuid().ToString());
                writer.WriteString("visitorId", evt.VisitorId);
                writer.WriteString("experimentId", evt.ExperimentId);
                writer.WriteString("variationId", evt.VariationId);
                writer.WriteString("eventName", evt.EventName);
                // revenue is REQUIRED and nullable: null on an exposure, never omitted.
                if (evt.Revenue is null) writer.WriteNull("revenue");
                else writer.WriteNumber("revenue", evt.Revenue.Value);
                writer.WriteNumber("timestamp", evt.Timestamp);
                writer.WriteString("pageUrl", evt.PageUrl ?? string.Empty);
                // value, properties, and attributes are OPTIONAL, which in the ingestion schema
                // means absent rather than null. Sending null fails validation for the whole batch.
                if (evt.Value is not null) writer.WriteNumber("value", evt.Value.Value);
                if (evt.Properties is { Count: > 0 })
                {
                    writer.WriteStartObject("properties");
                    foreach (KeyValuePair<string, object?> property in evt.Properties)
                    {
                        WriteUnknown(writer, property.Key, property.Value);
                    }
                    writer.WriteEndObject();
                }
                if (evt.Attributes is { Count: > 0 })
                {
                    writer.WriteStartObject("attributes");
                    foreach (KeyValuePair<string, string> attribute in evt.Attributes)
                    {
                        writer.WriteString(attribute.Key, attribute.Value);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// {sdkKey, metricEvents: [...]}. Note there is no properties field: the metric batch has
    /// none, and anything sent under that name is stripped before storage, so it would look
    /// delivered and be gone.
    /// </summary>
    internal string MetricBatchBody(IReadOnlyList<MetricEvent> events)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sdkKey", _options.SdkKey);
            writer.WriteStartArray("metricEvents");
            foreach (MetricEvent evt in events)
            {
                writer.WriteStartObject();
                writer.WriteString("eventId", evt.EventId ?? Guid.NewGuid().ToString());
                writer.WriteString("visitorId", evt.VisitorId);
                writer.WriteString("eventName", evt.EventName);
                if (evt.Revenue is null) writer.WriteNull("revenue");
                else writer.WriteNumber("revenue", evt.Revenue.Value);
                writer.WriteNumber("timestamp", evt.Timestamp);
                writer.WriteString("pageUrl", evt.PageUrl ?? string.Empty);
                if (evt.Value is not null) writer.WriteNumber("value", evt.Value.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>{sdkKey, orders: [...]}.</summary>
    internal string OrdersBody(string visitorId, PurchaseOrder order, long timestamp)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sdkKey", _options.SdkKey);
            writer.WriteStartArray("orders");
            writer.WriteStartObject();
            writer.WriteString("eventId", Guid.NewGuid().ToString());
            writer.WriteString("visitorId", visitorId ?? string.Empty);
            writer.WriteString("orderId", order.OrderId);
            writer.WriteNumber("total", order.Total);
            writer.WriteNumber("timestamp", timestamp);
            writer.WriteString("pageUrl", string.Empty);
            if (order.Currency is not null) writer.WriteString("currency", order.Currency);
            if (order.Subtotal is not null) writer.WriteNumber("subtotal", order.Subtotal.Value);
            if (order.Shipping is not null) writer.WriteNumber("shipping", order.Shipping.Value);
            if (order.Tax is not null) writer.WriteNumber("tax", order.Tax.Value);
            if (order.Discount is not null) writer.WriteNumber("discount", order.Discount.Value);
            if (order.Coupon is not null) writer.WriteString("coupon", order.Coupon);
            if (order.Test is not null) writer.WriteBoolean("test", order.Test.Value);
            if (order.Items is { Count: > 0 })
            {
                writer.WriteStartArray("items");
                foreach (PurchaseOrderItem item in order.Items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sku", item.Sku);
                    if (item.ProductKey is not null) writer.WriteString("productKey", item.ProductKey);
                    if (item.Name is not null) writer.WriteString("name", item.Name);
                    if (item.Price is not null) writer.WriteNumber("price", item.Price.Value);
                    if (item.Quantity is not null) writer.WriteNumber("quantity", item.Quantity.Value);
                    if (item.Category is not null) writer.WriteString("category", item.Category);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteUnknown(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(key); break;
            case string s: writer.WriteString(key, s); break;
            case bool b: writer.WriteBoolean(key, b); break;
            case int i: writer.WriteNumber(key, i); break;
            case long l: writer.WriteNumber(key, l); break;
            case double d: writer.WriteNumber(key, d); break;
            default:
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        TimeSpan interval = _options.FlushInterval < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : _options.FlushInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A flush failure must never kill the timer: the next cycle is the retry.
                _options.Logger.Warn("Auto-flush failed.", ex);
            }
        }
    }

    private async Task FlushSafelyAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Logger.Warn("Batch-triggered flush failed.", ex);
        }
    }

    private async Task PostAsync(string url, string body, int itemCount, string what)
    {
        Backoff.State? backoff = null;
        long? pendingRetryAfterMs = null;

        for (int attempt = 0; attempt <= _options.RetryAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("User-Agent", SdkMetadata.UserAgent);

                using HttpResponseMessage response =
                    await _http.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _options.Logger.Debug($"Flushed {itemCount} {what} to {url}.");
                    return;
                }

                pendingRetryAfterMs = response.Headers.TryGetValues("retry-after", out var values)
                    ? Backoff.ParseRetryAfter(values.FirstOrDefault())
                    : null;

                if (attempt >= _options.RetryAttempts)
                {
                    _options.Logger.Error($"Failed to send {itemCount} {what} to {url} after"
                        + $" {_options.RetryAttempts} retries: HTTP {(int)response.StatusCode}.");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (attempt >= _options.RetryAttempts)
                {
                    _options.Logger.Error($"Failed to send {itemCount} {what} to {url} after"
                        + $" {_options.RetryAttempts} retries.", ex);
                    return;
                }
            }

            Backoff.State next = Backoff.Next(backoff, pendingRetryAfterMs);
            backoff = next;
            pendingRetryAfterMs = null;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(next.DelayMs), _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Queue helpers
    // -----------------------------------------------------------------------

    private int EffectiveBatchSize => Math.Max(1, Math.Min(_options.BatchSize, MaxBatchSize));

    private void DropOldest<T>(List<T> queue, string what)
    {
        int max = Math.Max(1, _options.MaxQueueSize);
        if (queue.Count <= max) return;
        int dropped = queue.Count - max;
        queue.RemoveRange(0, dropped);
        _options.Logger.Warn(
            $"{what} queue full ({max}), dropping {dropped} oldest events.");
    }
}
