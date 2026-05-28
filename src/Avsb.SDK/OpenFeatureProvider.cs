// OpenFeatureProvider.cs
// Implements the OpenFeature FeatureProvider contract so AvsB integrates
// with any OpenFeature-aware framework.
//
// Requires: OpenFeature 1.5+ (declared with PrivateAssets="all" in the csproj).

using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;

namespace Avsb.SDK;

/// <summary>
/// OpenFeature provider backed by an AvsbServer instance.
/// </summary>
public sealed class AvsbOpenFeatureProvider : FeatureProvider
{
    private readonly AvsbServer _server;

    public override Metadata GetMetadata() => new("AvsB");

    public AvsbOpenFeatureProvider(AvsbServer server) => _server = server;

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = ToEvalContext(context);
        var flag = _server.GetBoolFlag(flagKey, defaultValue, ctx);
        return Task.FromResult(ToResolutionDetails(flagKey, flag));
    }

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = ToEvalContext(context);
        var flag = _server.GetStringFlag(flagKey, defaultValue, ctx);
        return Task.FromResult(ToResolutionDetails(flagKey, flag));
    }

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = ToEvalContext(context);
        var raw = _server.GetNumberFlag(flagKey, defaultValue, ctx);
        return Task.FromResult(new ResolutionDetails<int>(
            flagKey, (int)raw.Value, MapReason(raw.Source), raw.VariationKey));
    }

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = ToEvalContext(context);
        var flag = _server.GetNumberFlag(flagKey, defaultValue, ctx);
        return Task.FromResult(ToResolutionDetails(flagKey, flag));
    }

    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var ctx = ToEvalContext(context);
        var raw = _server.GetStringFlag(flagKey, string.Empty, ctx);
        Value value = string.IsNullOrEmpty(raw.Value)
            ? defaultValue
            : new Value(raw.Value);
        return Task.FromResult(new ResolutionDetails<Value>(
            flagKey, value, MapReason(raw.Source), raw.VariationKey));
    }

    private static ResolutionDetails<T> ToResolutionDetails<T>(string key, Flag<T> flag) =>
        new(key, flag.Value, MapReason(flag.Source), flag.VariationKey);

    // OpenFeature uses string-typed reason constants (Reason.TargetingMatch etc.).
    private static string MapReason(EvaluationSource source) => source switch
    {
        EvaluationSource.Rule or EvaluationSource.Holdout or EvaluationSource.Bandit =>
            Reason.TargetingMatch,
        EvaluationSource.DatafileOverride or EvaluationSource.RuntimeOverride =>
            Reason.Override,
        EvaluationSource.Sticky => Reason.CachedEvaluation,
        EvaluationSource.Default => Reason.Default,
        EvaluationSource.NotFound => Reason.Unknown,
        EvaluationSource.Disabled => Reason.Disabled,
        _ => Reason.Unknown,
    };

    private static EvalContext? ToEvalContext(EvaluationContext? ctx)
    {
        if (ctx is null) return null;
        // OpenFeature stores the targeting key as EvaluationContext.TargetingKey.
        var key = ctx.TargetingKey ?? string.Empty;
        var attrs = new Dictionary<string, object?>();
        foreach (var entry in ctx)
        {
            if (entry.Key == "targetingKey") continue;
            attrs[entry.Key] = entry.Value?.AsObject;
        }
        return EvalContext.User(key, attributes: attrs);
    }
}
