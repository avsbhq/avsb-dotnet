using System.Text.Json;

namespace Avsb.SDK.Core;

/// <summary>
/// Internal flag evaluator. Mirrors evaluateFlag in evaluate.ts.
/// </summary>
internal static class Evaluator
{
    public readonly struct EvalResult
    {
        public object? Value { get; init; }
        public string? VariationKey { get; init; }
        public EvaluationSource Source { get; init; }
        public string? RuleId { get; init; }
        public RuleType? RuleType { get; init; }
        public IReadOnlyList<string> Reasons { get; init; }
    }

    public static EvalResult EvaluateFlag(
        string flagKey,
        object? defaultValue,
        EvalContext ctx,
        FlagDatafile datafile,
        List<string>? reasons = null)
    {
        reasons ??= new List<string>();

        // 1. FIND FLAG
        var flag = datafile.Flags.Find(f => f.Key == flagKey);
        if (flag is null)
        {
            reasons.Add($"flag \"{flagKey}\" not found in datafile");
            return new EvalResult
            {
                Value = defaultValue,
                VariationKey = null,
                Source = EvaluationSource.NotFound,
                RuleId = null,
                RuleType = null,
                Reasons = reasons,
            };
        }

        // 2. CHECK ENABLED
        if (!flag.Enabled)
        {
            reasons.Add($"flag \"{flagKey}\" is disabled, returning default variation");
            var dv = GetVariationValue(flag, flag.DefaultVariationId);
            return new EvalResult
            {
                Value = dv?.Item1 ?? defaultValue,
                VariationKey = dv?.Item2,
                Source = EvaluationSource.Disabled,
                RuleId = null,
                RuleType = null,
                Reasons = reasons,
            };
        }

        // 3. CHECK OVERRIDES (match on primary context key / user key)
        string primaryKey = ContextHelper.BucketingKey(ctx, "user");
        if (string.IsNullOrEmpty(primaryKey))
        {
            // Fall back to the first available context's key.
            var primary = ctx.IsMulti
                ? ctx.Multi!.Contexts.Values.FirstOrDefault()
                : ctx.Single;
            primaryKey = primary?.Key ?? string.Empty;
        }

        var overrideEntry = flag.Overrides.Find(o => o.UserId == primaryKey);
        if (overrideEntry is not null)
        {
            reasons.Add($"matched user override for userId=\"{primaryKey}\"");
            var ov = GetVariationValue(flag, overrideEntry.VariationId);
            return new EvalResult
            {
                Value = ov?.Item1 ?? defaultValue,
                VariationKey = ov?.Item2,
                Source = EvaluationSource.DatafileOverride,
                RuleId = null,
                RuleType = null,
                Reasons = reasons,
            };
        }

        // 4. HOLDOUT CHECK
        var holdout = HoldoutEvaluator.EvaluateHoldout(ctx, flag.Id, datafile);
        if (holdout.HeldOut && holdout.Holdout is not null)
        {
            string targetVarId = holdout.VariationId ?? flag.DefaultVariationId;
            var hv = GetVariationValue(flag, targetVarId);
            reasons.Add($"held out by holdout \"{holdout.Holdout.Key}\", serving default variation \"{hv?.Item2 ?? targetVarId}\"");
            return new EvalResult
            {
                Value = hv?.Item1 ?? defaultValue,
                VariationKey = hv?.Item2,
                Source = EvaluationSource.Holdout,
                RuleId = holdout.Holdout.Id,
                RuleType = SDK.RuleType.Holdout,
                Reasons = reasons,
            };
        }

        // Build audience map
        var audienceMap = datafile.Audiences.ToDictionary(a => a.Id);

        // 5. EVALUATE RULES (first match wins)
        foreach (var rule in flag.Rules)
        {
            if (!rule.Enabled)
            {
                reasons.Add($"rule \"{rule.Id}\" skipped (disabled)");
                continue;
            }

            string hashPath = string.IsNullOrEmpty(rule.HashAttribute) ? "userId" : rule.HashAttribute;
            object? resolvedObj = ContextHelper.ResolveDottedAttribute(ctx, hashPath);
            string hashValue = resolvedObj?.ToString() ?? string.Empty;

            // a. AUDIENCE CHECK
            if (rule.AudienceIds.Count > 0)
            {
                bool matched = rule.AudienceIds.Any(id =>
                    audienceMap.TryGetValue(id, out var aud) &&
                    AudienceEvaluator.EvaluateAudience(aud, ctx));

                if (!matched)
                {
                    reasons.Add($"rule \"{rule.Id}\" skipped (audience not matched)");
                    continue;
                }
            }

            // b. EVALUATE BY TYPE
            switch (rule.Type)
            {
                case "targeted_delivery":
                {
                    if (rule.Variations.Count == 0) continue;
                    var tv = GetVariationValue(flag, rule.Variations[0].VariationId);
                    reasons.Add($"rule \"{rule.Id}\" matched (targeted_delivery), serving variation \"{tv?.Item2}\"");
                    return new EvalResult
                    {
                        Value = tv?.Item1 ?? defaultValue,
                        VariationKey = tv?.Item2,
                        Source = EvaluationSource.Rule,
                        RuleId = rule.Id,
                        RuleType = SDK.RuleType.TargetedDelivery,
                        Reasons = reasons,
                    };
                }

                case "ab_test":
                {
                    // Traffic allocation check
                    uint allocHash = Hash.Murmurhash3(hashValue + flagKey + "__" + rule.Id) % 10_000;
                    if (rule.TrafficAllocation is not null &&
                        allocHash >= (uint)(rule.TrafficAllocation.Value * 10_000))
                    {
                        reasons.Add($"rule \"{rule.Id}\" excluded by traffic allocation (hash {allocHash} >= threshold {(uint)(rule.TrafficAllocation.Value * 10_000)})");
                        continue;
                    }

                    // Variation assignment
                    uint hash = Hash.Murmurhash3(hashValue + flagKey) % 10_000;
                    double cumulative = 0;
                    foreach (var rv in rule.Variations)
                    {
                        cumulative += rv.Percentage * 10_000;
                        if (hash < cumulative)
                        {
                            var expVar = GetVariationValue(flag, rv.VariationId);
                            reasons.Add($"rule \"{rule.Id}\" matched (ab_test), bucketed into variation \"{expVar?.Item2}\" (hash {hash}/{(uint)cumulative})");
                            return new EvalResult
                            {
                                Value = expVar?.Item1 ?? defaultValue,
                                VariationKey = expVar?.Item2,
                                Source = EvaluationSource.Rule,
                                RuleId = rule.Id,
                                RuleType = SDK.RuleType.AbTest,
                                Reasons = reasons,
                            };
                        }
                    }
                    continue;
                }
            }
        }

        // 6. DEFAULT
        reasons.Add("no rules matched, returning default variation");
        var defVar = GetVariationValue(flag, flag.DefaultVariationId);
        return new EvalResult
        {
            Value = defVar?.Item1 ?? defaultValue,
            VariationKey = defVar?.Item2,
            Source = EvaluationSource.Default,
            RuleId = null,
            RuleType = null,
            Reasons = reasons,
        };
    }

    /// <summary>
    /// Resolve a variation value to a typed CLR object + key.
    /// JsonElement is unwrapped to string/bool/double/null.
    /// </summary>
    private static (object? Value, string Key)? GetVariationValue(
        FlagDatafileEntry flag, string variationId)
    {
        var variation = flag.Variations.Find(v => v.Id == variationId);
        if (variation is null) return null;
        return (UnwrapJson(variation.Value), variation.Key);
    }

    internal static object? UnwrapJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => (object)true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => el.TryGetDouble(out var d) ? d : (object?)null,
        JsonValueKind.String => el.GetString(),
        _ => el.GetRawText(),
    };
}
