using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Logger;
using Avsb.SDK.Tracking;

namespace Avsb.SDK;

// AvsbServer, continued (FU-31): the client-layer evaluation pipeline, sticky bucketing and type coercion.
public sealed partial class AvsbServer
{
    // -----------------------------------------------------------------------
    // Internal evaluation.
    // -----------------------------------------------------------------------

    private delegate bool Coercer<T>(object? raw, out T value, out string mismatch);

    private Flag<T> Evaluate<T>(
        string key, T defaultValue, EvalContext? context, string declaredType, Coercer<T> coerce)
    {
        long started = Stopwatch.GetTimestamp();
        FlagDatafile? datafile = _datafile;

        if (datafile is null)
        {
            string reason = _disposed
                ? $"AvsB SDK is disposed, so flag \"{key}\" cannot be evaluated. Build a new"
                    + " AvsbServer if you need flags again."
                : $"AvsB SDK is not ready: no datafile is loaded, so flag \"{key}\" fell back to"
                    + " your default. Await InitAsync (or set Bootstrap) before evaluating.";
            _logger.Warn(reason);
            return Flag<T>.NotReady(defaultValue, reason);
        }

        EvalContext ctx = context ?? EvalContext.User(string.Empty);
        WarnOnEmptyContextOnce(ctx);

        var reasons = new List<string>();
        Evaluator.EvalResult result = EvaluateRaw(datafile, key, defaultValue, ctx, reasons);
        RecordDecision(key, ctx, result, started);

        // The datafile's declared type wins over guessing: asking for a bool on a string flag is a
        // configuration mistake, and saying so beats returning a coerced value.
        FlagDatafileEntry? entry = datafile.Flags.Find(f => f.Key == key);
        if (entry is not null && !string.IsNullOrEmpty(entry.Type) && entry.Type != declaredType)
        {
            string reason = $"flag \"{key}\" is declared as \"{entry.Type}\" in the datafile but a"
                + $" \"{declaredType}\" value was requested; returning your default. Use"
                + $" {GetterName(entry.Type)} instead.";
            _logger.Warn(reason);
            return Flag<T>.NotFound(defaultValue, reason);
        }

        if (!coerce(result.Value, out T typed, out string mismatch))
        {
            string reason = $"flag \"{key}\" {mismatch}; returning your default.";
            _logger.Warn(reason);
            return Flag<T>.NotFound(defaultValue, reason);
        }

        return BuildFlag(typed, result, started);
    }

    /// <summary>The three client-layer steps plus the evaluator, with no type coercion.</summary>
    private Evaluator.EvalResult EvaluateRaw(
        FlagDatafile datafile, string key, object? defaultValue, EvalContext ctx,
        List<string> reasons)
    {
        // 1. Runtime override, per flag key, resolved against the datafile's variations.
        Evaluator.EvalResult? runtime = ResolveRuntimeOverride(datafile, key, reasons);
        if (runtime is not null) return runtime.Value;

        // 2. Sticky lookup, before the evaluator, so a stored assignment beats a rule and even
        //    beats a datafile override (the node pattern, locked by the corpus).
        Evaluator.EvalResult? sticky = ResolveSticky(datafile, key, ctx, reasons);
        if (sticky is not null) return sticky.Value;

        // 3. The evaluator. The caller's default goes in, so a datafile that cannot answer hands
        //    back the caller's own value with the real source rather than something invented.
        var options = new EvaluateOptions
        {
            OnExposure = _tracker is null ? null : _tracker.Queue,
            BanditModelStore = _options.BanditModelStore,
        };
        Evaluator.EvalResult result =
            Evaluator.EvaluateFlag(key, defaultValue, ctx, datafile, reasons, options);
        PersistStickyAssignment(datafile, key, ctx, result);
        return result;
    }

    /// <summary>
    /// A runtime override is a flag key mapped to a VARIATION KEY, resolved against the datafile so
    /// the served value and its key are real. A key that names no variation is logged and ignored
    /// rather than serving something invented.
    /// </summary>
    private Evaluator.EvalResult? ResolveRuntimeOverride(
        FlagDatafile datafile, string flagKey, List<string> reasons)
    {
        if (_options.RuntimeOverrides is null
            || !_options.RuntimeOverrides.TryGetValue(flagKey, out string? variationKey))
        {
            return null;
        }

        FlagDatafileEntry? flag = datafile.Flags.Find(f => f.Key == flagKey);
        if (flag is null)
        {
            _logger.Warn($"Runtime override for flag \"{flagKey}\" was ignored: that flag is not in"
                + " the datafile for this environment.");
            return null;
        }
        FlagDatafileVariation? variation = flag.Variations.Find(v => v.Key == variationKey);
        if (variation is null)
        {
            _logger.Warn($"Runtime override for flag \"{flagKey}\" was ignored: variation"
                + $" \"{variationKey}\" is not one of that flag's variations"
                + $" ({string.Join(", ", flag.Variations.Select(v => v.Key))}).");
            return null;
        }

        reasons.Add($"matched runtime override for flagKey=\"{flagKey}\"");
        return new Evaluator.EvalResult
        {
            Value = variation.Value,
            VariationKey = variation.Key,
            Source = EvaluationSource.RuntimeOverride,
            RuleId = null,
            RuleType = null,
            Reasons = reasons,
        };
    }

    /// <summary>
    /// Sticky lookup. Runs BEFORE the evaluator, so a stored assignment wins over rules and over a
    /// datafile override: a user who was already bucketed stays bucketed. Nothing ever called
    /// Lookup before this.
    ///
    /// A stored variation that no longer exists is a MISS, not an error. A stored variation whose
    /// rule is gone is still served, with a warning and no exposure, because the rule it would be
    /// attributed to no longer exists.
    /// </summary>
    private Evaluator.EvalResult? ResolveSticky(
        FlagDatafile datafile, string flagKey, EvalContext ctx, List<string> reasons)
    {
        if (_options.StickyBucketService is null) return null;

        string userId = ContextHelper.BucketingKey(ctx);
        if (string.IsNullOrEmpty(userId)) return null;

        StickyAssignment? assignment = _options.StickyBucketService.Lookup(userId, flagKey);
        if (assignment is null) return null;

        FlagDatafileEntry? flag = datafile.Flags.Find(f => f.Key == flagKey);
        FlagDatafileVariation? stored = flag?.Variations.Find(v => v.Id == assignment.VariationId);
        if (flag is null || stored is null) return null;

        bool ruleStillPresent = flag.Rules
            .Where(r => r.Id == assignment.RuleId)
            .Any(r => r.Variations.Any(rv => rv.VariationId == stored.Id));
        if (!ruleStillPresent)
        {
            _logger.Warn($"Sticky assignment for flag \"{flagKey}\" references a rule that no longer"
                + " serves that variation, so the stored value is being served without an exposure"
                + $" event (userId={userId}, ruleId={assignment.RuleId}).");
        }

        reasons.Add($"matched sticky bucket assignment for userId=\"{userId}\"");
        return new Evaluator.EvalResult
        {
            Value = stored.Value,
            VariationKey = stored.Key,
            Source = EvaluationSource.Sticky,
            RuleId = assignment.RuleId,
            RuleType = assignment.RuleType,
            Reasons = reasons,
        };
    }

    /// <summary>
    /// Persist a sticky assignment. ONLY for an ab_test rule that served a variation, and the
    /// stored field is the variation's ID.
    ///
    /// Both halves were wrong before: it saved on every rule type including holdouts, and it saved
    /// the variation KEY into VariationId. Lookup resolves by id, so the first real use produced a
    /// corrupted store and no sticky behaviour at all.
    /// </summary>
    private void PersistStickyAssignment(
        FlagDatafile datafile, string flagKey, EvalContext ctx, Evaluator.EvalResult result)
    {
        if (_options.StickyBucketService is null) return;
        if (result.Source != EvaluationSource.Rule
            || result.RuleType != SDK.RuleType.AbTest
            || result.RuleId is null
            || result.VariationKey is null)
        {
            return;
        }

        string userId = ContextHelper.BucketingKey(ctx);
        if (string.IsNullOrEmpty(userId)) return;

        FlagDatafileEntry? flag = datafile.Flags.Find(f => f.Key == flagKey);
        FlagDatafileVariation? variation = flag?.Variations.Find(v => v.Key == result.VariationKey);
        if (variation is null) return;

        _options.StickyBucketService.Save(userId, flagKey, new StickyAssignment
        {
            VariationId = variation.Id,
            RuleId = result.RuleId,
            RuleType = result.RuleType.Value,
            AssignedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>
    /// Warn once per client about an empty bucketing key. Every caller who forgets the context
    /// lands the whole fleet in one bucket, which corrupts an experiment invisibly, so the message
    /// names the exact call to add.
    /// </summary>
    private void WarnOnEmptyContextOnce(EvalContext ctx)
    {
        if (!string.IsNullOrEmpty(ContextHelper.BucketingKey(ctx))) return;
        if (Interlocked.Exchange(ref _emptyContextWarned, 1) != 0) return;
        _logger.Warn("AvsB evaluated a flag with an empty bucketing key, so every request will land"
            + " in the same bucket and experiment results will be wrong. Pass a real identity:"
            + " EvalContext.User(\"<your user id>\").");
    }

    // -----------------------------------------------------------------------
    // Type coercion.
    // -----------------------------------------------------------------------

    private static bool TryCoerceBool(object? raw, out bool value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case JsonElement { ValueKind: JsonValueKind.True }:
                value = true;
                return true;
            case JsonElement { ValueKind: JsonValueKind.False }:
                value = false;
                return true;
            default:
                value = false;
                mismatch = $"resolved to {DescribeValue(raw)}, not a boolean";
                return false;
        }
    }

    private static bool TryCoerceString(object? raw, out string value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case string s:
                value = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                value = element.GetString() ?? string.Empty;
                return true;
            default:
                value = string.Empty;
                mismatch = $"resolved to {DescribeValue(raw)}, not a string";
                return false;
        }
    }

    private static bool TryCoerceDouble(object? raw, out double value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case double d:
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case float f:
                value = f;
                return true;
            case decimal dec:
                value = (double)dec;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                value = element.GetDouble();
                return true;
            default:
                value = 0;
                mismatch = $"resolved to {DescribeValue(raw)}, not a number";
                return false;
        }
    }

    private static bool TryCoerceJson<T>(object? raw, out T? value, out string mismatch)
    {
        mismatch = string.Empty;
        switch (raw)
        {
            case null:
                value = default;
                return true;
            case T typed:
                value = typed;
                return true;
            case JsonElement element:
                try
                {
                    value = element.Deserialize<T>(JsonOptions.Default);
                    return true;
                }
                catch (JsonException ex)
                {
                    value = default;
                    mismatch = "resolved to a JSON document that does not map to "
                        + typeof(T).Name + " (" + ex.Message + ")";
                    return false;
                }
            default:
                value = default;
                mismatch = $"resolved to {DescribeValue(raw)}, which does not map to {typeof(T).Name}";
                return false;
        }
    }

    private static string DescribeValue(object? raw) => raw switch
    {
        null => "null",
        bool => "a boolean",
        string => "a string",
        double or int or long or float or decimal => "a number",
        JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } => "a boolean",
        JsonElement { ValueKind: JsonValueKind.String } => "a string",
        JsonElement { ValueKind: JsonValueKind.Number } => "a number",
        JsonElement { ValueKind: JsonValueKind.Array } => "a JSON array",
        JsonElement { ValueKind: JsonValueKind.Object } => "a JSON object",
        JsonElement { ValueKind: JsonValueKind.Null } => "null",
        _ => "a " + raw.GetType().Name,
    };

    private static string GetterName(string declaredType) => declaredType switch
    {
        "boolean" => "GetBoolFlag",
        "string" => "GetStringFlag",
        "number" => "GetNumberFlag",
        "json" => "GetJsonFlag",
        _ => "GetJsonFlag",
    };

    private static Flag<T> BuildFlag<T>(T value, Evaluator.EvalResult result, long startTimestamp) =>
        new()
        {
            Value = value,
            VariationKey = result.VariationKey,
            Source = result.Source,
            RuleId = result.RuleId,
            RuleType = result.RuleType,
            Reasons = result.Reasons,
            EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DurationMicros = ElapsedMicros(startTimestamp),
        };

    private static long ElapsedMicros(long startTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return Math.Max(0, (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency));
    }
}
