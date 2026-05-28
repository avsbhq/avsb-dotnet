namespace Avsb.SDK;

/// <summary>Persisted sticky bucketing assignment. Mirrors §2.4.</summary>
public sealed record StickyAssignment
{
    public string VariationId { get; init; } = string.Empty;
    public string RuleId { get; init; } = string.Empty;
    public RuleType RuleType { get; init; }

    /// <summary>ms-epoch (Unix milliseconds) when assigned.</summary>
    public long AssignedAt { get; init; }
}

/// <summary>
/// Service that persists sticky bucket assignments so that users stay in
/// the same variation across re-evaluations. Mirrors §2.4 StickyBucketService.
/// </summary>
public interface IStickyBucketService
{
    /// <summary>Synchronous lookup — hot-path safe.</summary>
    StickyAssignment? Lookup(string userId, string flagKey);

    /// <summary>Persist an assignment. Implementations may fire-and-forget.</summary>
    void Save(string userId, string flagKey, StickyAssignment assignment);
}
