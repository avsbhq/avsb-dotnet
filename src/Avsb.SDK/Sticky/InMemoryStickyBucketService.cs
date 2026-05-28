namespace Avsb.SDK.Sticky;

/// <summary>
/// Thread-safe in-memory sticky bucket service. Suitable for testing and
/// single-process deployments where cross-instance consistency is not required.
/// </summary>
public sealed class InMemoryStickyBucketService : IStickyBucketService
{
    private readonly Dictionary<string, StickyAssignment> _store = new();
    private readonly object _lock = new();

    public StickyAssignment? Lookup(string userId, string flagKey)
    {
        lock (_lock)
        {
            _store.TryGetValue(Key(userId, flagKey), out var result);
            return result;
        }
    }

    public void Save(string userId, string flagKey, StickyAssignment assignment)
    {
        lock (_lock) _store[Key(userId, flagKey)] = assignment;
    }

    private static string Key(string userId, string flagKey) => $"{userId}:{flagKey}";
}
