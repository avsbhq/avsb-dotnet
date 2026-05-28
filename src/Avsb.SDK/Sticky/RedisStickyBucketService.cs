// RedisStickyBucketService.cs
// Requires StackExchange.Redis 2.7+. Compiled only when the host project
// includes a reference to StackExchange.Redis — the core SDK declares it
// with PrivateAssets="all" so it does not transitively force the dep.

#if AVSB_REDIS_STICKY

using StackExchange.Redis;
using System.Text.Json;

namespace Avsb.SDK.Sticky;

/// <summary>
/// Redis-backed sticky bucket service. Thread-safe. Keys expire after
/// <paramref name="ttl"/> (default 30 days) to prevent unbounded growth.
/// </summary>
public sealed class RedisStickyBucketService : IStickyBucketService
{
    private readonly IDatabase _db;
    private readonly TimeSpan _ttl;
    private const string Prefix = "avsb:sticky:";

    public RedisStickyBucketService(IDatabase db, TimeSpan? ttl = null)
    {
        _db = db;
        _ttl = ttl ?? TimeSpan.FromDays(30);
    }

    public StickyAssignment? Lookup(string userId, string flagKey)
    {
        var raw = _db.StringGet(Prefix + Key(userId, flagKey));
        if (raw.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<StickyAssignment>(raw!);
    }

    public void Save(string userId, string flagKey, StickyAssignment assignment)
    {
        var json = JsonSerializer.Serialize(assignment);
        _db.StringSet(Prefix + Key(userId, flagKey), json, _ttl);
    }

    private static string Key(string userId, string flagKey) => $"{userId}:{flagKey}";
}

#endif
