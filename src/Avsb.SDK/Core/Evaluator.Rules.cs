using System.Text.Json;

namespace Avsb.SDK.Core;

// Evaluator, continued (FU-31): step 5 of EvaluateFlag, one rule against one context, and the
// bandit resolution that step needs. EvaluateFlag walks the rules in declared order and stops at
// the first one that answers.
public static partial class Evaluator
{
    /// <summary>
    /// Evaluate one rule. Returns the decision when the rule matches, or null when evaluation falls
    /// through to the next rule (disabled, audience not matched, outside the allocation, or nothing
    /// to serve). Every skip that the reference names appends its reason.
    /// </summary>
    private static EvalResult? EvaluateRule(
        FlagDatafileRule rule,
        FlagDatafileEntry flag,
        string flagKey,
        object? defaultValue,
        EvalContext ctx,
        FlagDatafile datafile,
        Dictionary<string, FlagDatafileAudience> audienceMap,
        int currencyExponent,
        List<string> reasons,
        EvaluateOptions options)
    {
        if (!rule.Enabled)
        {
            reasons.Add($"rule \"{rule.Id}\" skipped (disabled)");
            return null;
        }

        string hashPath = string.IsNullOrEmpty(rule.HashAttribute) ? "userId" : rule.HashAttribute;
        object? resolvedObj = ContextHelper.ResolveDottedAttribute(ctx, hashPath);
        // JavaScript coercion: a numeric hash attribute must stringify to "42", not "42.0",
        // or this process buckets the user differently from every other SDK.
        string hashValue = resolvedObj is null ? string.Empty : JsNumber.StringOf(resolvedObj);

        // a. AUDIENCE CHECK
        if (rule.AudienceIds.Count > 0)
        {
            bool matched = rule.AudienceIds.Any(id =>
                audienceMap.TryGetValue(id, out FlagDatafileAudience? aud) &&
                AudienceEvaluator.EvaluateAudience(aud, ctx, currencyExponent));

            if (!matched)
            {
                reasons.Add($"rule \"{rule.Id}\" skipped (audience not matched)");
                return null;
            }
        }

        // b. EVALUATE BY TYPE
        switch (rule.Type)
        {
            case "targeted_delivery":
            {
                // An empty variation list cannot serve anything. The reference skips it
                // without a reason, so this does too.
                if (rule.Variations.Count == 0) return null;
                // A targeted delivery rule below 100 percent is a ROLLOUT: serve the variation
                // to that share of the audience and let everyone else fall through to the next
                // rule, exactly as an ab_test rule's allocation does (N1-1). The gate sits after
                // the empty-variations skip, so a rule with nothing to serve stays silent.
                string? tdExclusion = AllocationExclusion(rule, hashValue, flagKey);
                if (tdExclusion is not null)
                {
                    reasons.Add(tdExclusion);
                    return null;
                }
                var tv = GetVariationValue(flag, rule.Variations[0].VariationId);
                reasons.Add($"rule \"{rule.Id}\" matched (targeted_delivery), serving variation"
                    + $" \"{KeyText(tv)}\"");
                return new EvalResult
                {
                    Value = tv is null ? defaultValue : tv.Value.Value,
                    VariationKey = tv?.Key,
                    Source = EvaluationSource.Rule,
                    RuleId = rule.Id,
                    RuleType = SDK.RuleType.TargetedDelivery,
                    Reasons = reasons,
                };
            }

            case "ab_test":
            {
                // Traffic allocation (see AllocationExclusion for the float-threshold rule).
                string? abExclusion = AllocationExclusion(rule, hashValue, flagKey);
                if (abExclusion is not null)
                {
                    reasons.Add(abExclusion);
                    return null;
                }

                uint hash = Hash.Murmurhash3(hashValue + flagKey) % 10_000;
                double cumulative = 0;
                foreach (RuleVariationSlot rv in rule.Variations)
                {
                    cumulative += rv.Percentage * 10_000;
                    if (hash < cumulative)
                    {
                        var expVar = GetVariationValue(flag, rv.VariationId);

                        if (expVar is not null)
                        {
                            options.OnExposure?.Invoke(new ExposureEvent
                            {
                                VisitorId = hashValue,
                                ExperimentId = rule.Id,
                                VariationId = rv.VariationId,
                                EventName = ExposureEvent.FlagExposure,
                                Revenue = null,
                                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                PageUrl = string.Empty,
                                Properties = new Dictionary<string, object?>
                                {
                                    ["flagKey"] = flagKey,
                                    ["flagId"] = flag.Id,
                                },
                                Attributes = BuildExposureAttributes(ctx, datafile),
                            });
                        }

                        reasons.Add($"rule \"{rule.Id}\" matched (ab_test), bucketed into"
                            + $" variation \"{KeyText(expVar)}\" (hash {hash}/"
                            + $"{JsNumber.Stringify(cumulative)})");
                        return new EvalResult
                        {
                            Value = expVar is null ? defaultValue : expVar.Value.Value,
                            VariationKey = expVar?.Key,
                            Source = EvaluationSource.Rule,
                            RuleId = rule.Id,
                            RuleType = SDK.RuleType.AbTest,
                            Reasons = reasons,
                        };
                    }
                }
                // Fell through every variation: the percentages do not sum to the allocated
                // traffic. The reference continues silently.
                return null;
            }

            case "bandit":
            {
                // Every exit below appends a reason. A bandit rule must never disappear from
                // the decision trail (spec section 1.2 silent-drop ban): before this, a
                // configured bandit behaved exactly like "no rules matched" and this switch
                // had no case for it at all.
                (string Key, BanditConfig Config)? bandit = ResolveRuleBandit(rule, datafile);
                if (bandit is null)
                {
                    reasons.Add(!string.IsNullOrEmpty(rule.BanditId)
                        ? $"rule \"{rule.Id}\" skipped (banditId \"{rule.BanditId}\" is not in"
                            + " datafile.bandits)"
                        : $"rule \"{rule.Id}\" skipped (bandit rule carries no bandit config)");
                    return null;
                }
                if (bandit.Value.Config.Actions.Count == 0)
                {
                    reasons.Add($"rule \"{rule.Id}\" skipped (bandit \"{bandit.Value.Key}\""
                        + " declares no actions)");
                    return null;
                }

                IAvsbBanditModelStore store = options.BanditModelStore
                    ?? new ColdStartBanditStore(bandit.Value.Config);
                var decision = BanditEvaluator.EvaluateBandit(
                    bandit.Value.Key, bandit.Value.Config, ctx, store);
                if (decision is null)
                {
                    reasons.Add($"rule \"{rule.Id}\" skipped (bandit \"{bandit.Value.Key}\" has"
                        + " no model in the supplied store)");
                    return null;
                }

                var banditVar = GetVariationValue(flag, decision.Action.VariationId);
                if (banditVar is null)
                {
                    reasons.Add($"rule \"{rule.Id}\" skipped (bandit \"{bandit.Value.Key}\""
                        + $" action \"{decision.Action.Id}\" targets unknown variation"
                        + $" \"{decision.Action.VariationId}\")");
                    return null;
                }

                options.OnExposure?.Invoke(new ExposureEvent
                {
                    VisitorId = hashValue,
                    ExperimentId = rule.Id,
                    VariationId = decision.Action.VariationId,
                    EventName = ExposureEvent.FlagExposure,
                    Revenue = null,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    PageUrl = string.Empty,
                    Properties = new Dictionary<string, object?>
                    {
                        ["flagKey"] = flagKey,
                        ["flagId"] = flag.Id,
                        ["banditKey"] = bandit.Value.Key,
                        ["actionId"] = decision.Action.Id,
                    },
                    Attributes = BuildExposureAttributes(ctx, datafile),
                });

                reasons.Add($"rule \"{rule.Id}\" matched (bandit \"{bandit.Value.Key}\"), served"
                    + $" action \"{decision.Action.Id}\" into variation \"{banditVar.Value.Key}\"");
                return new EvalResult
                {
                    Value = banditVar.Value.Value,
                    VariationKey = banditVar.Value.Key,
                    Source = EvaluationSource.Bandit,
                    RuleId = rule.Id,
                    RuleType = SDK.RuleType.Bandit,
                    Reasons = reasons,
                };
            }

            default:
            {
                // A rule type this SDK version does not know: name it rather than dropping it,
                // so an old SDK against a new datafile is diagnosable.
                reasons.Add($"rule \"{rule.Id}\" skipped (unsupported rule type \"{rule.Type}\")");
                return null;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Bandit resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve the bandit a bandit-typed rule points at. A shared banditId joined against
    /// datafile.bandits wins over an inline rule.bandit, and the resolved key is what addresses
    /// the model store: the shared entry's key, or the rule's own id for an inline config.
    /// </summary>
    private static (string Key, BanditConfig Config)? ResolveRuleBandit(
        FlagDatafileRule rule, FlagDatafile datafile)
    {
        if (!string.IsNullOrEmpty(rule.BanditId))
        {
            FlagDatafileBandit? shared = datafile.Bandits?.Find(b => b.Id == rule.BanditId);
            return shared is null ? null : (shared.Key, shared.Config);
        }
        if (rule.Bandit is not null)
        {
            return (rule.Id, rule.Bandit);
        }
        return null;
    }
}
