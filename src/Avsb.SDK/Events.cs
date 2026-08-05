namespace Avsb.SDK;

/// <summary>
/// One exposure event, exactly as the ingestion API accepts it.
///
/// Property names are FROZEN VOCABULARY (spec section 1.1, locked by the wire-shape conformance
/// fixture family). The ingestion worker validates these exact names and the dashboard reads the
/// columns they land in, so a renamed field is silently dropped data, not a compile error.
/// </summary>
public sealed record ExposureEvent
{
    /// <summary>Event name for a flag rule or bandit exposure.</summary>
    public const string FlagExposure = "flag_exposure";

    /// <summary>Event name for a holdout exposure.</summary>
    public const string FlagHoldoutExposure = "flag_holdout_exposure";

    /// <summary>
    /// UUID stamped once at enqueue time, so a retried batch carries the same ids and ingestion
    /// can deduplicate. Null until the tracker stamps it.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>The bucketing identity the decision was made for.</summary>
    public string VisitorId { get; init; } = string.Empty;

    /// <summary>The rule id for a flag exposure, the holdout id for a holdout exposure.</summary>
    public string ExperimentId { get; init; } = string.Empty;

    public string VariationId { get; init; } = string.Empty;

    /// <summary>flag_exposure or flag_holdout_exposure.</summary>
    public string EventName { get; init; } = FlagExposure;

    /// <summary>Always null on an exposure. Only conversions and orders carry money.</summary>
    public double? Revenue { get; init; }

    /// <summary>Optional numeric value. Null on an exposure.</summary>
    public double? Value { get; init; }

    /// <summary>Milliseconds since the Unix epoch.</summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Required by the ingestion schema; an empty string is what a server SDK sends, because
    /// there is no page on a server.
    /// </summary>
    public string PageUrl { get; init; } = string.Empty;

    /// <summary>
    /// Decision metadata: flagKey and flagId, plus holdoutId and holdoutKey for a holdout, or
    /// banditKey and actionId for a bandit.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }

    /// <summary>
    /// Registered attributes only. Unregistered keys are dropped server-side, so registering an
    /// attribute in the dashboard is what makes it queryable.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

/// <summary>
/// One conversion or metric event, exactly as the ingestion API accepts it.
///
/// There is deliberately NO properties field. Unlike the exposure batch, the metric batch has
/// none: anything sent under that name is stripped before storage, so it would look delivered and
/// be gone. Put the number in <see cref="Value"/> instead.
///
/// There is also no experiment or variation: attribution happens server-side from the visitor's
/// recorded exposures.
/// </summary>
public sealed record MetricEvent
{
    public string? EventId { get; init; }

    public string VisitorId { get; init; } = string.Empty;

    /// <summary>
    /// The metric key as configured in the dashboard. An unknown key is accepted and stored, so a
    /// typo shows up as an empty metric rather than an error.
    /// </summary>
    public string EventName { get; init; } = string.Empty;

    /// <summary>Decimal major units, or null for a non-revenue conversion.</summary>
    public double? Revenue { get; init; }

    /// <summary>Numeric metric value for average-value metrics; null when the metric is a count.</summary>
    public double? Value { get; init; }

    public long Timestamp { get; init; }

    public string PageUrl { get; init; } = string.Empty;
}

/// <summary>
/// A purchase, exactly as the ingestion API accepts it. Sent immediately rather than batched on a
/// timer, because an order is the event a customer least wants buffered.
///
/// Money is DECIMAL MAJOR UNITS as displayed (99.95, not 9995). <see cref="Total"/> must be finite
/// and greater than zero; a zero or negative total is rejected by ingestion.
/// </summary>
public sealed record PurchaseOrder
{
    /// <summary>Required. Unique order or transaction identifier.</summary>
    public string OrderId { get; init; } = string.Empty;

    /// <summary>Required. Order total in major units.</summary>
    public double Total { get; init; }

    /// <summary>ISO 4217 code.</summary>
    public string? Currency { get; init; }

    public double? Subtotal { get; init; }
    public double? Shipping { get; init; }
    public double? Tax { get; init; }
    public double? Discount { get; init; }
    public string? Coupon { get; init; }

    /// <summary>
    /// True stores the order but excludes it from every revenue, profit, recommendation, and
    /// audience calculation. Only send it when you are deliberately testing the integration.
    /// </summary>
    public bool? Test { get; init; }

    public IReadOnlyList<PurchaseOrderItem>? Items { get; init; }
}

/// <summary>One line item on a <see cref="PurchaseOrder"/>.</summary>
public sealed record PurchaseOrderItem
{
    /// <summary>Required. Stock keeping unit.</summary>
    public string Sku { get; init; } = string.Empty;

    /// <summary>
    /// Canonical product-grain key. Pass it when known so order items join catalog and
    /// recommendation analytics without alias resolution; ingestion derives it from Sku otherwise.
    /// </summary>
    public string? ProductKey { get; init; }

    public string? Name { get; init; }
    public double? Price { get; init; }
    public int? Quantity { get; init; }
    public string? Category { get; init; }
}

/// <summary>Optional extras for <c>AvsbServer.Track</c>.</summary>
public sealed record TrackPayload
{
    /// <summary>Numeric metric value, for average-value metrics. Null for a plain count.</summary>
    public double? Value { get; init; }

    /// <summary>
    /// Money in DECIMAL MAJOR UNITS as displayed (9.99, not 999). Null for a non-revenue
    /// conversion. For a full order with line items use <c>AvsbServer.TrackPurchase</c> instead.
    /// </summary>
    public double? Revenue { get; init; }

    /// <summary>Identity override for this one event.</summary>
    public EvalContext? Context { get; init; }
}
