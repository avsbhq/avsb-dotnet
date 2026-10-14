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
/// EvaluatorAllocation.cs, and the per-rule step of the evaluation in Evaluator.Rules.cs.
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

        // 5. EVALUATE RULES (first match wins). Each rule's body lives in Evaluator.Rules.cs; a
        //    null answer falls through to the next rule.
        foreach (FlagDatafileRule rule in flag.Rules)
        {
            EvalResult? decision = EvaluateRule(
                rule, flag, flagKey, defaultValue, ctx, datafile, audienceMap, currencyExponent,
                reasons, options);
            if (decision is not null) return decision.Value;
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
