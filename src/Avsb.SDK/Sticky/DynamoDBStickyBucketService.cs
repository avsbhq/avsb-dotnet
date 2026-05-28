// DynamoDBStickyBucketService.cs
// Requires AWSSDK.DynamoDBv2. Compiled only when the host project includes
// a reference to AWSSDK.DynamoDBv2 — the core SDK declares it with
// PrivateAssets="all" so it does not transitively force the dep.

#if AVSB_DYNAMODB_STICKY

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using System.Text.Json;

namespace Avsb.SDK.Sticky;

/// <summary>
/// DynamoDB-backed sticky bucket service. Each assignment is stored as a
/// single item with hash key = userId and range key = flagKey.
/// TTL attribute "ttlEpoch" enables automatic expiry in DynamoDB.
/// </summary>
public sealed class DynamoDBStickyBucketService : IStickyBucketService
{
    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly TimeSpan _ttl;

    public DynamoDBStickyBucketService(
        IAmazonDynamoDB client,
        string tableName,
        TimeSpan? ttl = null)
    {
        _client = client;
        _tableName = tableName;
        _ttl = ttl ?? TimeSpan.FromDays(30);
    }

    public StickyAssignment? Lookup(string userId, string flagKey)
    {
        // DynamoDB SDK is async-only; run synchronously on the hot path
        // using a dedicated thread to avoid deadlocks in ASP.NET contexts.
        return Task.Run(() => LookupAsync(userId, flagKey)).GetAwaiter().GetResult();
    }

    private async Task<StickyAssignment?> LookupAsync(string userId, string flagKey)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new AttributeValue { S = userId },
                ["flagKey"] = new AttributeValue { S = flagKey },
            },
        };
        var response = await _client.GetItemAsync(request).ConfigureAwait(false);
        if (!response.IsItemSet) return null;
        if (!response.Item.TryGetValue("payload", out var payloadAttr)) return null;
        return JsonSerializer.Deserialize<StickyAssignment>(payloadAttr.S);
    }

    public void Save(string userId, string flagKey, StickyAssignment assignment)
    {
        Task.Run(() => SaveAsync(userId, flagKey, assignment)).GetAwaiter().GetResult();
    }

    private async Task SaveAsync(string userId, string flagKey, StickyAssignment assignment)
    {
        long ttlEpoch = DateTimeOffset.UtcNow.Add(_ttl).ToUnixTimeSeconds();
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new AttributeValue { S = userId },
                ["flagKey"] = new AttributeValue { S = flagKey },
                ["payload"] = new AttributeValue { S = JsonSerializer.Serialize(assignment) },
                ["ttlEpoch"] = new AttributeValue { N = ttlEpoch.ToString() },
            },
        };
        await _client.PutItemAsync(request).ConfigureAwait(false);
    }
}

#endif
