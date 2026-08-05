using System.Text.Json;
using System.Text.Json.Serialization;

namespace Avsb.SDK.Core;

// -----------------------------------------------------------------------
// FlagDatafile: the wire shape of the AvsB datafile, mirroring types.ts.
//
// Property names are the wire contract. Renaming one silently stops reading the
// field: System.Text.Json has nothing to bind and the value stays at its default,
// which is how banditId rules and commerce audiences used to evaluate as "no
// match" with no error anywhere.
// -----------------------------------------------------------------------

public sealed class FlagDatafile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("projectType")] public string ProjectType { get; set; } = string.Empty;
    [JsonPropertyName("sdkKey")] public string SdkKey { get; set; } = string.Empty;
    [JsonPropertyName("environmentKey")] public string EnvironmentKey { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;

    /// <summary>Ingest origin for this environment. Normalise through Endpoints.CollectUrl.</summary>
    [JsonPropertyName("collectEndpoint")] public string CollectEndpoint { get; set; } = string.Empty;

    /// <summary>Where liveness pings go. Absent means do not send them.</summary>
    [JsonPropertyName("heartbeatEndpoint")] public string? HeartbeatEndpoint { get; set; }

    /// <summary>
    /// Server-controlled streaming ticket endpoint. Absent means streaming is unavailable;
    /// never guess a host.
    /// </summary>
    [JsonPropertyName("streamEndpoint")] public string? StreamEndpoint { get; set; }

    [JsonPropertyName("flags")] public List<FlagDatafileEntry> Flags { get; set; } = new();
    [JsonPropertyName("audiences")] public List<FlagDatafileAudience> Audiences { get; set; } = new();
    [JsonPropertyName("holdouts")] public List<FlagDatafileHoldout>? Holdouts { get; set; }

    /// <summary>Shared bandits a rule can join through banditId.</summary>
    [JsonPropertyName("bandits")] public List<FlagDatafileBandit>? Bandits { get; set; }

    [JsonPropertyName("contextKinds")] public List<string>? ContextKinds { get; set; }

    /// <summary>
    /// Attribute keys the project registered. Exposure events carry only these; unregistered
    /// keys are dropped server-side, so registering an attribute in the dashboard is what makes
    /// it queryable. Accepts both wire shapes: v1 publishes strings, v2 publishes objects.
    /// </summary>
    [JsonPropertyName("registeredAttributes")]
    public List<JsonElement>? RegisteredAttributes { get; set; }

    [JsonPropertyName("exposureSampling")] public double? ExposureSampling { get; set; }

    /// <summary>
    /// Minor-unit exponent for commerce money conditions. Absent means 2. A zero-exponent
    /// currency such as JPY publishes 0, and a port that hardcodes two decimals matches every
    /// visitor who spent a hundredth of the intended threshold.
    /// </summary>
    [JsonPropertyName("currencyExponent")] public int? CurrencyExponent { get; set; }

    public static FlagDatafile? Deserialize(string json) =>
        JsonSerializer.Deserialize<FlagDatafile>(json, JsonOptions.Default);

    /// <summary>Registered attribute keys, flattened out of either wire shape.</summary>
    public IReadOnlyList<string> RegisteredAttributeKeys()
    {
        if (RegisteredAttributes is null || RegisteredAttributes.Count == 0)
        {
            return Array.Empty<string>();
        }
        var keys = new List<string>(RegisteredAttributes.Count);
        foreach (JsonElement entry in RegisteredAttributes)
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                string? value = entry.GetString();
                if (!string.IsNullOrEmpty(value)) keys.Add(value!);
            }
            else if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("key", out JsonElement keyProp)
                && keyProp.ValueKind == JsonValueKind.String)
            {
                string? value = keyProp.GetString();
                if (!string.IsNullOrEmpty(value)) keys.Add(value!);
            }
        }
        return keys;
    }
}

public sealed class FlagDatafileEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The declared value type: boolean, string, number, or json. The typed getters check the
    /// resolved value against it.
    /// </summary>
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

    /// <summary>
    /// targeted_delivery, ab_test, or bandit. An unrecognised value is skipped with a reason
    /// naming it, never dropped silently.
    /// </summary>
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("audienceIds")] public List<string> AudienceIds { get; set; } = new();
    [JsonPropertyName("hashAttribute")] public string HashAttribute { get; set; } = string.Empty;
    [JsonPropertyName("holdoutId")] public string? HoldoutId { get; set; }

    /// <summary>Joins datafile.bandits. Wins over <see cref="Bandit"/>.</summary>
    [JsonPropertyName("banditId")] public string? BanditId { get; set; }

    /// <summary>Inline bandit config, addressed in the model store by the rule id.</summary>
    [JsonPropertyName("bandit")] public BanditConfig? Bandit { get; set; }

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
    /// Each element is either a Condition or a nested ConditionGroup. Deserialised as raw
    /// JsonElement and resolved lazily to avoid a custom converter on the hot path.
    /// </summary>
    [JsonPropertyName("conditions")] public List<JsonElement> Conditions { get; set; } = new();
}

public sealed class Condition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    /// <summary>
    /// CUSTOM_ATTRIBUTE, one of the three commerce families, or a browser-only family a server
    /// cannot answer.
    /// </summary>
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    [JsonPropertyName("attribute")] public string? Attribute { get; set; }
    [JsonPropertyName("operator")] public string Operator { get; set; } = string.Empty;
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
    [JsonPropertyName("contextKind")] public string? ContextKind { get; set; }

    /// <summary>
    /// Commerce passthrough: "sku" (default) or "category", picking which viewed-list attribute
    /// a PRODUCTS_VIEWED condition reads.
    /// </summary>
    [JsonPropertyName("match")] public string? Match { get; set; }

    /// <summary>
    /// Commerce passthrough: dataset-backed membership lives in the browser, so a condition
    /// carrying one evaluates false server-side for both operators.
    /// </summary>
    [JsonPropertyName("datasetSlug")] public string? DatasetSlug { get; set; }
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

/// <summary>
/// A shared bandit. Key (not the id, and not the rule id) is what addresses the model store and
/// what the decision is attributed to.
/// </summary>
public sealed class FlagDatafileBandit
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("config")] public BanditConfig Config { get; set; } = new();
}

public sealed class BanditConfig
{
    /// <summary>epsilon-greedy, thompson-sampling, or ucb1.</summary>
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = string.Empty;

    /// <summary>Null means 0.1; an explicit 0 means exploration off.</summary>
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
