using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avsb.SDK.Core;

// -----------------------------------------------------------------------
// FlagDatafile — JSON-serialisable representation of the AvsB datafile.
// Mirrors the FlagDatafile wire shape from types.ts §2.15.
// All property names are camelCase on the wire; System.Text.Json handles
// this via JsonPropertyName attributes or a global naming policy.
// -----------------------------------------------------------------------

public sealed class FlagDatafile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("projectType")] public string ProjectType { get; set; } = string.Empty;
    [JsonPropertyName("sdkKey")] public string SdkKey { get; set; } = string.Empty;
    [JsonPropertyName("environmentKey")] public string EnvironmentKey { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;
    [JsonPropertyName("collectEndpoint")] public string CollectEndpoint { get; set; } = string.Empty;
    [JsonPropertyName("heartbeatEndpoint")] public string? HeartbeatEndpoint { get; set; }
    [JsonPropertyName("flags")] public List<FlagDatafileEntry> Flags { get; set; } = new();
    [JsonPropertyName("audiences")] public List<FlagDatafileAudience> Audiences { get; set; } = new();
    [JsonPropertyName("holdouts")] public List<FlagDatafileHoldout>? Holdouts { get; set; }
    [JsonPropertyName("bandits")] public List<FlagDatafileBandit>? Bandits { get; set; }
    [JsonPropertyName("contextKinds")] public List<string>? ContextKinds { get; set; }
    [JsonPropertyName("exposureSampling")] public double? ExposureSampling { get; set; }

    public static FlagDatafile? Deserialize(string json) =>
        JsonSerializer.Deserialize<FlagDatafile>(json, JsonOptions.Default);
}

public sealed class FlagDatafileEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("defaultVariationId")] public string DefaultVariationId { get; set; } = string.Empty;
    [JsonPropertyName("variations")] public List<FlagDatafileVariation> Variations { get; set; } = new();
    [JsonPropertyName("overrides")] public List<FlagDatafileOverride> Overrides { get; set; } = new();
    [JsonPropertyName("rules")] public List<FlagDatafileRule> Rules { get; set; } = new();
}

public sealed class FlagDatafileVariation
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

public sealed class FlagDatafileOverride
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("variationId")] public string VariationId { get; set; } = string.Empty;
}

public sealed class FlagDatafileRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("audienceIds")] public List<string> AudienceIds { get; set; } = new();
    [JsonPropertyName("hashAttribute")] public string HashAttribute { get; set; } = string.Empty;
    [JsonPropertyName("holdoutId")] public string? HoldoutId { get; set; }
    [JsonPropertyName("trafficAllocation")] public double? TrafficAllocation { get; set; }
    [JsonPropertyName("variations")] public List<RuleVariationSlot> Variations { get; set; } = new();
}

public sealed class RuleVariationSlot
{
    [JsonPropertyName("variationId")] public string VariationId { get; set; } = string.Empty;
    [JsonPropertyName("percentage")] public double Percentage { get; set; }
}

public sealed class FlagDatafileAudience
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("rules")] public ConditionGroup? Rules { get; set; }
}

public sealed class ConditionGroup
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("logic")] public string Logic { get; set; } = "AND";

    /// <summary>
    /// Each element is either a Condition or a nested ConditionGroup.
    /// Deserialised as raw JsonElement and resolved lazily to avoid a
    /// custom converter on the hot path.
    /// </summary>
    [JsonPropertyName("conditions")] public List<JsonElement> Conditions { get; set; } = new();
}

public sealed class Condition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("attribute")] public string? Attribute { get; set; }
    [JsonPropertyName("operator")] public string Operator { get; set; } = string.Empty;
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonPropertyName("contextKind")] public string? ContextKind { get; set; }
}

public sealed class FlagDatafileHoldout
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("trafficAllocation")] public double TrafficAllocation { get; set; }
    [JsonPropertyName("flagIds")] public List<string> FlagIds { get; set; } = new();
    [JsonPropertyName("hashAttribute")] public string HashAttribute { get; set; } = string.Empty;
    [JsonPropertyName("defaultVariationByFlag")] public Dictionary<string, string> DefaultVariationByFlag { get; set; } = new();
}

public sealed class FlagDatafileBandit
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("config")] public BanditConfig Config { get; set; } = new();
}

public sealed class BanditConfig
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = string.Empty;
    [JsonPropertyName("explorationRate")] public double? ExplorationRate { get; set; }
    [JsonPropertyName("rewardMetric")] public string RewardMetric { get; set; } = string.Empty;
    [JsonPropertyName("actions")] public List<BanditActionDef> Actions { get; set; } = new();
}

public sealed class BanditActionDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("variationId")] public string VariationId { get; set; } = string.Empty;
    [JsonPropertyName("contextAttributes")] public Dictionary<string, JsonElement>? ContextAttributes { get; set; }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
