namespace Avsb.SDK.Core;

/// <summary>
/// Abstraction for big-segment membership. Mirrors §2.9 BigSegmentStore.
/// </summary>
public interface IBigSegmentStore
{
    /// <summary>Synchronous membership check; hot-path safe.</summary>
    bool IsMember(string segmentKey, string contextKey);

    /// <summary>Bulk membership for one context, keyed by segmentKey.</summary>
    IReadOnlyDictionary<string, bool> Membership(string contextKey);

    /// <summary>False when the store is stale or unavailable.</summary>
    bool IsHealthy();
}

/// <summary>
/// In-memory big-segment store. Thread-safe; safe as a singleton.
/// </summary>
public sealed class InMemoryBigSegmentStore : IBigSegmentStore
{
    private readonly Dictionary<string, Dictionary<string, bool>> _data = new();
    private readonly object _lock = new();
    private bool _healthy = true;

    /// <summary>Pre-populate membership data for a context key.</summary>
    public void SetMembership(string contextKey, Dictionary<string, bool> membership)
    {
        lock (_lock) _data[contextKey] = new Dictionary<string, bool>(membership);
    }

    public void SetHealthy(bool healthy)
    {
        lock (_lock) _healthy = healthy;
    }

    public bool IsMember(string segmentKey, string contextKey)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(contextKey, out var membership)) return false;
            return membership.TryGetValue(segmentKey, out var result) && result;
        }
    }

    public IReadOnlyDictionary<string, bool> Membership(string contextKey)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(contextKey, out var membership))
                return new Dictionary<string, bool>();
            return new Dictionary<string, bool>(membership);
        }
    }

    public bool IsHealthy()
    {
        lock (_lock) return _healthy;
    }
}
