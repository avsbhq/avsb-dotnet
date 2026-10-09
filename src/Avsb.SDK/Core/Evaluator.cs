using System.Text.Json;

namespace Avsb.SDK.Core;

/// <summary>
/// Core flag evaluation engine. Mirrors evaluateFlag in evaluate.ts.
///
/// Evaluation order, identical in every SDK: flag not found, flag disabled, datafile user
/// override, holdout, rules in declared order (including bandit dispatch), flag default variation.
///
/// Runtime overrides and sticky bucketing sit ABOVE this function, at the client layer (see
/// AvsbServer), exactly as @avsbhq/node arranges them. This function stays a pure function of
/// datafile plus context.
///
/// <see cref="EvalResult.Value"/> is the datafile's variation value as a <see cref="JsonElement"/>
/// when a variation was served, and the caller's own default object otherwise. Keeping the
/// JsonElement is what lets GetJsonFlag&lt;T&gt; deserialise directly instead of round-tripping
/// through a string, and it is why a JSON flag no longer has to be read as a string first.
///
/// Every reason string here is byte-compared against the reference by the conformance corpus, in
/// order. Numbers inside them go through <see cref="JsNumber"/> because they were produced by
/// JavaScript string interpolation, and because a plain ToString() would emit a comma decimal
/// separator on a machine with a European culture.
///
/// The traffic-allocation gate shared by ab_test and targeted_delivery rules lives in
/// EvaluatorAllocation.cs.
/// </summary>
public static partial class Evaluator
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
        List<string>? reasons = null,
        EvaluateOptions? options = null)
    {
        reasons ??= new List<string>();
        options ??= EvaluateOptions.None;

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
                Value = dv is null ? defaultValue : dv.Value.Value,
                VariationKey = dv?.Key,
                Source = EvaluationSource.Disabled,
                RuleId = null,
                RuleType = null,
                Reasons = reasons,
            };
        }

        // 3. CHECK OVERRIDES. Overrides match on the primary context's key (legacy "userId").
        //    For a multi-context evaluation the primary kind is "user", or the first declared
        //    kind when no user sub-context is present.
        string primaryKey = ContextHelper.BucketingKey(ctx, "user");
        if (string.IsNullOrEmpty(primaryKey))
        {
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
                Value = ov is null ? defaultValue : ov.Value.Value,
                VariationKey = ov?.Key,
                Source = EvaluationSource.DatafileOverride,
                RuleId = null,
                RuleType = null,
                Reasons = reasons,
            };
        }

        // 4. HOLDOUT CHECK. Fires between overrides and the rules loop so an explicit operator
        //    override still wins, while every regular rule honours the holdout.
        var holdout = HoldoutEvaluator.EvaluateHoldout(ctx, flag.Id, datafile);
        if (holdout.HeldOut && holdout.Holdout is not null)
        {
            string targetVarId = holdout.VariationId ?? flag.DefaultVariationId;
            var hv = GetVariationValue(flag, targetVarId);

            options.OnExposure?.Invoke(new ExposureEvent
            {
                VisitorId = primaryKey,
                ExperimentId = holdout.Holdout.Id,
                VariationId = targetVarId,
                EventName = ExposureEvent.FlagHoldoutExposure,
                Revenue = null,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PageUrl = string.Empty,
                Properties = new Dictionary<string, object?>
                {
                    ["flagKey"] = flagKey,
                    ["flagId"] = flag.Id,
                    ["holdoutId"] = holdout.Holdout.Id,
                    ["holdoutKey"] = holdout.Holdout.Key,
                },
                Attributes = BuildExposureAttributes(ctx, datafile),
            });

            reasons.Add($"held out by holdout \"{holdout.Holdout.Key}\", serving default variation"
                + $" \"{hv?.Key ?? targetVarId}\"");
            return new EvalResult
            {
                Value = hv is null ? defaultValue : hv.Value.Value,
                VariationKey = hv?.Key,
                Source = EvaluationSource.Holdout,
                RuleId = holdout.Holdout.Id,
                RuleType = SDK.RuleType.Holdout,
                Reasons = reasons,
            };
        }

        var audienceMap = datafile.Audiences.ToDictionary(a => a.Id);
        int currencyExponent = AudienceCommerce.CurrencyExponentOf(datafile.CurrencyExponent);

        // 5. EVALUATE RULES (first match wins)
        foreach (FlagDatafileRule rule in flag.Rules)
        {
            if (!rule.Enabled)
            {
                reasons.Add($"rule \"{rule.Id}\" skipped (disabled)");
                continue;
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
                    continue;
                }
            }

            // b. EVALUATE BY TYPE
            switch (rule.Type)
            {
                case "targeted_delivery":
                {
                    // An empty variation list cannot serve anything. The reference skips it
                    // without a reason, so this does too.
                    if (rule.Variations.Count == 0) continue;
                    // A targeted delivery rule below 100 percent is a ROLLOUT: serve the variation
                    // to that share of the audience and let everyone else fall through to the next
                    // rule, exactly as an ab_test rule's allocation does (N1-1). The gate sits after
                    // the empty-variations skip, so a rule with nothing to serve stays silent.
                    string? tdExclusion = AllocationExclusion(rule, hashValue, flagKey);
                    if (tdExclusion is not null)
                    {
                        reasons.Add(tdExclusion);
                        continue;
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
                        continue;
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
                    continue;
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
                        continue;
                    }
                    if (bandit.Value.Config.Actions.Count == 0)
                    {
                        reasons.Add($"rule \"{rule.Id}\" skipped (bandit \"{bandit.Value.Key}\""
                            + " declares no actions)");
                        continue;
                    }

                    IAvsbBanditModelStore store = options.BanditModelStore
                        ?? new ColdStartBanditStore(bandit.Value.Config);
                    var decision = BanditEvaluator.EvaluateBandit(
                        bandit.Value.Key, bandit.Value.Config, ctx, store);
                    if (decision is null)
                    {
                        reasons.Add($"rule \"{rule.Id}\" skipped (bandit \"{bandit.Value.Key}\" has"
                            + " no model in the supplied store)");
                        continue;
                    }

                    var banditVar = GetVariationValue(flag, decision.Action.VariationId);
                    if (banditVar is null)
                    {
                        reasons.Add($"rule \"{rule.Id}\" skipped (bandit \"{bandit.Value.Key}\""
                            + $" action \"{decision.Action.Id}\" targets unknown variation"
                            + $" \"{decision.Action.VariationId}\")");
                        continue;
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
                    continue;
                }
            }
        }

        // 6. DEFAULT
        reasons.Add("no rules matched, returning default variation");
        var defVar = GetVariationValue(flag, flag.DefaultVariationId);
        return new EvalResult
        {
            Value = defVar is null ? defaultValue : defVar.Value.Value,
            VariationKey = defVar?.Key,
            Source = EvaluationSource.Default,
            RuleId = null,
            RuleType = null,
            Reasons = reasons,
        };
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

    // -----------------------------------------------------------------------
    // Exposure attributes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Flatten the context and filter it down to the project's registered attribute keys,
    /// stringifying values so they fit the analytics store's string map.
    ///
    /// Unregistered keys are dropped: that is the privacy and cardinality guard, and it is why
    /// registering an attribute in the dashboard is what makes it queryable. Returns null when
    /// nothing survives, so the field is omitted from the wire body.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? BuildExposureAttributes(
        EvalContext ctx, FlagDatafile datafile)
    {
        IReadOnlyList<string> registered = datafile.RegisteredAttributeKeys();
        if (registered.Count == 0) return null;
        var allowed = new HashSet<string>(registered, StringComparer.Ordinal);

        var flattened = new Dictionary<string, object?>(StringComparer.Ordinal);
        SingleContext? primary = ctx.IsMulti
            ? (ctx.Multi!.Contexts.TryGetValue("user", out SingleContext? user)
                ? user
                : ctx.Multi.Contexts.Values.FirstOrDefault())
            : ctx.Single;
        if (primary is not null)
        {
            flattened["userId"] = primary.Key;
            if (primary.Name is not null) flattened["name"] = primary.Name;
            foreach (KeyValuePair<string, object?> attr in primary.Attributes)
            {
                if (IsReserved(attr.Key)) continue;
                flattened[attr.Key] = attr.Value;
            }
        }
        if (ctx.IsMulti)
        {
            foreach (KeyValuePair<string, SingleContext> sub in ctx.Multi!.Contexts)
            {
                flattened[$"{sub.Key}.key"] = sub.Value.Key;
                if (sub.Value.Name is not null) flattened[$"{sub.Key}.name"] = sub.Value.Name;
                foreach (KeyValuePair<string, object?> attr in sub.Value.Attributes)
                {
                    if (IsReserved(attr.Key)) continue;
                    flattened[$"{sub.Key}.{attr.Key}"] = attr.Value;
                }
            }
        }

        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> entry in flattened)
        {
            if (!allowed.Contains(entry.Key) || entry.Value is null) continue;
            output[entry.Key] = StringifyAttribute(entry.Value);
        }
        return output.Count == 0 ? null : output;
    }

    private static bool IsReserved(string key) =>
        key is "kind" or "key" or "name" or "_meta";

    private static string StringifyAttribute(object value)
    {
        if (value is IReadOnlyDictionary<string, object?> or IReadOnlyList<object?>)
        {
            try
            {
                return JsonSerializer.Serialize(value);
            }
            catch (NotSupportedException)
            {
                // An attribute that cannot be serialised is dropped rather than crashing an
                // evaluation.
                return string.Empty;
            }
        }
        return JsNumber.StringOf(value);
    }

    // -----------------------------------------------------------------------
    // Variation lookup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Look up a variation by id, keeping the raw <see cref="JsonElement"/>. Null when the id
    /// matches nothing, which is distinct from a variation whose value happens to be JSON null:
    /// only the first case falls back to the caller's default.
    /// </summary>
    private static (JsonElement Value, string Key)? GetVariationValue(
        FlagDatafileEntry flag, string variationId)
    {
        FlagDatafileVariation? variation = flag.Variations.Find(v => v.Id == variationId);
        if (variation is null) return null;
        return (variation.Value, variation.Key);
    }

    /// <summary>
    /// The variation key as the reference interpolates it into a reason string. A missing
    /// variation reads "undefined" there, because JavaScript stringifies optional chaining that
    /// way, and the corpus records it.
    /// </summary>
    private static string KeyText((JsonElement Value, string Key)? variation) =>
        variation?.Key ?? "undefined";

    /// <summary>
    /// Unwrap a JSON value into the plain CLR shapes the audience evaluator reads: bool, double,
    /// string, IReadOnlyList, IReadOnlyDictionary, or null.
    ///
    /// Objects and arrays used to come back as raw JSON TEXT, which is why every commerce
    /// condition failed: purchaseHistory was a string, not a dictionary, so no operator could
    /// read hasPurchased.
    /// </summary>
    public static object? UnwrapJson(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => el.TryGetDouble(out double d) ? d : (object?)null,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Array => UnwrapArray(el),
        JsonValueKind.Object => UnwrapObject(el),
        _ => el.GetRawText(),
    };

    private static IReadOnlyList<object?> UnwrapArray(JsonElement el)
    {
        var items = new List<object?>();
        foreach (JsonElement item in el.EnumerateArray()) items.Add(UnwrapJson(item));
        return items;
    }

    private static IReadOnlyDictionary<string, object?> UnwrapObject(JsonElement el)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in el.EnumerateObject())
        {
            map[property.Name] = UnwrapJson(property.Value);
        }
        return map;
    }
}
