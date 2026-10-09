// OpenFeatureProvider.cs
// Implements the OpenFeature FeatureProvider contract so AvsB integrates with any
// OpenFeature-aware framework.
//
// Requires: OpenFeature 2.0+. The Resolve*ValueAsync overrides below, with a CancellationToken,
// are the 2.x contract; 1.x named them Resolve*Value, so pinning 1.5 broke the net8.0 build.

using System.Text.Json;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Avsb.SDK;

/// <summary>
/// OpenFeature provider backed by an <see cref="AvsbServer"/>.
///
/// Context mapping, including the kinds OpenFeature has no native concept of:
///
/// - kind: "multi" plus one structure per kind builds a multi-context. Each structure's key (or
///   targetingKey) is that kind's bucketing key and its remaining members become that kind's
///   attributes.
/// - kind: "organization" with a targeting key builds a single context of that kind, so a flag
///   bucketed by organization buckets by organization.
/// - No kind builds a user context from the targeting key.
///
/// Collapsing everything to a user context, which this provider used to do, silently lost
/// organization-keyed bucketing: A vs B's headline targeting feature, evaluated as if it had never
/// been configured.
///
/// Error codes are mapped so an OpenFeature caller can tell the two failures apart: an SDK with no
/// datafile yet reports PROVIDER_NOT_READY, and a missing flag reports FLAG_NOT_FOUND. Those two
/// used to be indistinguishable.
/// </summary>
public sealed class AvsbOpenFeatureProvider : FeatureProvider
{
    private readonly AvsbServer _server;

    public AvsbOpenFeatureProvider(AvsbServer server) =>
        _server = server ?? throw new ArgumentNullException(nameof(server));

    public override Metadata GetMetadata() => new("AvsB");

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Flag<bool> flag = _server.GetBoolFlag(flagKey, defaultValue, ToEvalContext(context));
        return Task.FromResult(ToResolutionDetails(flagKey, flag, flag.Value));
    }

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Flag<string> flag = _server.GetStringFlag(flagKey, defaultValue, ToEvalContext(context));
        return Task.FromResult(ToResolutionDetails(flagKey, flag, flag.Value));
    }

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Flag<double> flag = _server.GetNumberFlag(flagKey, defaultValue, ToEvalContext(context));
        return Task.FromResult(ToResolutionDetails(flagKey, flag, (int)Math.Round(flag.Value)));
    }

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Flag<double> flag = _server.GetNumberFlag(flagKey, defaultValue, ToEvalContext(context));
        return Task.FromResult(ToResolutionDetails(flagKey, flag, flag.Value));
    }

    /// <summary>
    /// Resolve a JSON flag as an OpenFeature structure.
    ///
    /// This reads the flag as JSON. It used to read it as a STRING and wrap whatever came back, so
    /// a JSON flag holding a real object resolved to null and every caller got their default.
    /// </summary>
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Flag<JsonElement?> flag =
            _server.GetJsonFlag<JsonElement?>(flagKey, null, ToEvalContext(context));
        Value value = flag.Value is null ? defaultValue : ToValue(flag.Value.Value);
        return Task.FromResult(ToResolutionDetails(flagKey, flag, value));
    }

    // -----------------------------------------------------------------------
    // Result mapping.
    // -----------------------------------------------------------------------

    private static ResolutionDetails<TOut> ToResolutionDetails<TIn, TOut>(
        string key, Flag<TIn> flag, TOut value)
    {
        ErrorType errorType = flag.Source switch
        {
            EvaluationSource.NotReady => ErrorType.ProviderNotReady,
            EvaluationSource.NotFound => ErrorType.FlagNotFound,
            _ => ErrorType.None,
        };
        string? errorMessage = errorType == ErrorType.None || flag.Reasons is null
            || flag.Reasons.Count == 0
            ? null
            : flag.Reasons[0];

        return new ResolutionDetails<TOut>(
            key, value, errorType, MapReason(flag.Source), flag.VariationKey, errorMessage);
    }

    /// <summary>
    /// Map an A vs B source onto an OpenFeature reason. A vs B carries more detail than the
    /// OpenFeature vocabulary does, so several sources collapse; the precise source is still
    /// available on Flag.Source for callers holding the SDK directly.
    /// </summary>
    private static string MapReason(EvaluationSource source) => source switch
    {
        EvaluationSource.Rule or EvaluationSource.Holdout or EvaluationSource.Bandit =>
            Reason.TargetingMatch,
        EvaluationSource.DatafileOverride or EvaluationSource.RuntimeOverride =>
            Reason.TargetingMatch,
        EvaluationSource.Sticky => Reason.Cached,
        EvaluationSource.Default => Reason.Default,
        EvaluationSource.Disabled => Reason.Disabled,
        EvaluationSource.NotFound or EvaluationSource.NotReady => Reason.Error,
        _ => Reason.Unknown,
    };

    // -----------------------------------------------------------------------
    // Context mapping.
    // -----------------------------------------------------------------------

    /// <summary>Build an A vs B context from an OpenFeature one, honouring multi-kind.</summary>
    internal static EvalContext ToEvalContext(EvaluationContext? ctx)
    {
        if (ctx is null) return EvalContext.User(string.Empty);

        string key = ctx.TargetingKey ?? string.Empty;
        IReadOnlyDictionary<string, Value> members = ctx.AsDictionary();
        string? kind = members.TryGetValue("kind", out Value? kindValue) && kindValue.IsString
            ? kindValue.AsString
            : null;

        if (kind == "multi")
        {
            var contexts = new Dictionary<string, SingleContext>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Value> member in members)
            {
                if (member.Key is "kind" or "targetingKey") continue;
                if (!member.Value.IsStructure) continue;
                contexts[member.Key] = ToSubContext(member.Key, member.Value.AsStructure!);
            }
            if (contexts.Count > 0)
            {
                // Guarantee a user sub-context so a flag bucketed by user still buckets.
                if (!contexts.ContainsKey("user") && key.Length > 0)
                {
                    contexts["user"] = new SingleContext { Kind = "user", Key = key };
                }
                return EvalContext.FromMulti(new MultiContext { Contexts = contexts });
            }
            // "kind": "multi" with no structures is malformed; fall through to a single context
            // rather than throwing inside an evaluation.
        }

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Value> member in members)
        {
            if (member.Key is "kind" or "targetingKey") continue;
            attributes[member.Key] = ToClrObject(member.Value);
        }
        return EvalContext.FromSingle(new SingleContext
        {
            Kind = string.IsNullOrEmpty(kind) ? "user" : kind!,
            Key = key,
            Attributes = attributes,
        });
    }

    private static SingleContext ToSubContext(string kind, Structure structure)
    {
        IReadOnlyDictionary<string, Value> members = structure.AsDictionary();
        string key = members.TryGetValue("key", out Value? keyValue) && keyValue.IsString
            ? keyValue.AsString ?? string.Empty
            : members.TryGetValue("targetingKey", out Value? targeting) && targeting.IsString
                ? targeting.AsString ?? string.Empty
                : string.Empty;

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Value> member in members)
        {
            if (member.Key is "key" or "targetingKey" or "kind") continue;
            attributes[member.Key] = ToClrObject(member.Value);
        }
        return new SingleContext { Kind = kind, Key = key, Attributes = attributes };
    }

    /// <summary>OpenFeature Value to the plain CLR shapes the evaluator reads.</summary>
    private static object? ToClrObject(Value value)
    {
        if (value.IsNull) return null;
        if (value.IsBoolean) return value.AsBoolean;
        if (value.IsNumber) return value.AsDouble;
        if (value.IsString) return value.AsString;
        if (value.IsList)
        {
            var items = new List<object?>();
            foreach (Value item in value.AsList!) items.Add(ToClrObject(item));
            return items;
        }
        if (value.IsStructure)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Value> member in value.AsStructure!.AsDictionary())
            {
                map[member.Key] = ToClrObject(member.Value);
            }
            return map;
        }
        return value.AsObject;
    }

    /// <summary>A JSON document back to an OpenFeature Value, for structure evaluation.</summary>
    private static Value ToValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                return new Value(true);
            case JsonValueKind.False:
                return new Value(false);
            case JsonValueKind.String:
                return new Value(element.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                return new Value(element.GetDouble());
            case JsonValueKind.Array:
            {
                var items = new List<Value>();
                foreach (JsonElement item in element.EnumerateArray()) items.Add(ToValue(item));
                return new Value(items);
            }
            case JsonValueKind.Object:
            {
                var members = new Dictionary<string, Value>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    members[property.Name] = ToValue(property.Value);
                }
                return new Value(new Structure(members));
            }
            default:
                return new Value();
        }
    }
}
