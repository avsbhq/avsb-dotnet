using System.Text.Json;
using System.Text.RegularExpressions;

namespace Avsb.SDK.Core;

/// <summary>
/// Server-side audience evaluation. Mirrors evaluateAudience, evaluateGroup, and
/// evaluateCondition in audience.ts.
///
/// Three families of condition reach this evaluator: CUSTOM_ATTRIBUTE answered from the
/// evaluation context, the commerce families delegated to <see cref="AudienceCommerce"/>, and the
/// browser-detected families (DEVICE, BROWSER, PLATFORM, LANGUAGE, COOKIE, QUERY_PARAM) which a
/// server cannot answer and which therefore return false.
///
/// Operator aliases matter more than they look. The dashboard audience builder writes "is",
/// "is_not", "does_not_contain", and "does_not_exist" for the device-style condition types, and
/// those spellings reach CUSTOM_ATTRIBUTE conditions too. Before they were handled here, an
/// audience saved with "is" fell through to the default arm and matched nobody, with nothing
/// logged.
///
/// Comparisons go through <see cref="JsNumber"/> so a numeric attribute compares identically in
/// every SDK, and case-insensitive comparisons use the INVARIANT culture: a process running under
/// a Turkish culture would otherwise lowercase "I" to a dotless i and stop matching.
/// </summary>
internal static class AudienceEvaluator
{
    public static bool EvaluateAudience(FlagDatafileAudience audience, EvalContext ctx) =>
        EvaluateAudience(audience, ctx, AudienceCommerce.DefaultCurrencyExponent);

    public static bool EvaluateAudience(
        FlagDatafileAudience audience, EvalContext ctx, int currencyExponent)
    {
        if (audience.Rules is null) return true;
        return EvaluateGroup(audience.Rules, ctx, currencyExponent);
    }

    public static bool EvaluateGroup(ConditionGroup group, EvalContext ctx) =>
        EvaluateGroup(group, ctx, AudienceCommerce.DefaultCurrencyExponent);

    /// <summary>Evaluate a condition group. An empty group matches, mirroring the reference.</summary>
    public static bool EvaluateGroup(ConditionGroup group, EvalContext ctx, int currencyExponent)
    {
        if (group.Conditions.Count == 0) return true;

        bool isAnd = string.Equals(group.Logic, "AND", StringComparison.OrdinalIgnoreCase);

        foreach (JsonElement element in group.Conditions)
        {
            bool result = EvaluateElement(element, ctx, currencyExponent);
            if (isAnd && !result) return false;
            if (!isAnd && result) return true;
        }

        return isAnd;
    }

    private static bool EvaluateElement(JsonElement element, EvalContext ctx, int currencyExponent)
    {
        // A node carrying "logic" is a nested group; anything else is a condition.
        if (element.TryGetProperty("logic", out _))
        {
            var nested = element.Deserialize<ConditionGroup>(JsonOptions.Default);
            return nested is not null && EvaluateGroup(nested, ctx, currencyExponent);
        }

        var condition = element.Deserialize<Condition>(JsonOptions.Default);
        return condition is not null && EvaluateCondition(condition, ctx, currencyExponent);
    }

    public static bool EvaluateCondition(Condition condition, EvalContext ctx) =>
        EvaluateCondition(condition, ctx, AudienceCommerce.DefaultCurrencyExponent);

    public static bool EvaluateCondition(Condition condition, EvalContext ctx, int currencyExponent)
    {
        return condition.Type switch
        {
            "CUSTOM_ATTRIBUTE" => EvalCustomAttribute(condition, ctx),
            "CART_VALUE" or "PRODUCTS_VIEWED" or "PURCHASE_HISTORY" =>
                AudienceCommerce.EvaluateCommerceCondition(
                    condition,
                    attribute => ResolveAttribute(ctx, condition.ContextKind, attribute),
                    currencyExponent),
            // Device, browser, platform, language, cookie, and query-param conditions are
            // answered from a live browser. A server has none of those signals, so they are
            // false here rather than guessed.
            _ => false,
        };
    }

    private static bool EvalCustomAttribute(Condition condition, EvalContext ctx)
    {
        if (string.IsNullOrEmpty(condition.Attribute)) return false;

        // A declared contextKind that is absent from the context is a hard no-match. That is
        // NOT the same as a missing attribute, which can still satisfy does_not_exist.
        if (!string.IsNullOrEmpty(condition.ContextKind)
            && ContextHelper.GetContextByKind(ctx, condition.ContextKind!) is null)
        {
            return false;
        }

        object? actual = ResolveAttribute(ctx, condition.ContextKind, condition.Attribute!);
        string actualText = JsNumber.StringOf(actual);
        string expected = JsNumber.StringOf(ExpectedScalar(condition.Value));

        return condition.Operator switch
        {
            "exists" => actual is not null,
            "not_exists" or "does_not_exist" => actual is null,
            "equals" or "is" => actualText == expected,
            "not_equals" or "is_not" => actualText != expected,
            "contains" => actualText.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "not_contains" or "does_not_contain" =>
                !actualText.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "starts_with" => actualText.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "ends_with" => actualText.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "regex" => EvalRegex(actualText, expected),
            "greater_than" => JsNumber.NumberOf(actual) > JsNumber.NumberOf(expected),
            "less_than" => JsNumber.NumberOf(actual) < JsNumber.NumberOf(expected),
            "gte" => JsNumber.NumberOf(actual) >= JsNumber.NumberOf(expected),
            "lte" => JsNumber.NumberOf(actual) <= JsNumber.NumberOf(expected),
            "in_list" => AnyValueMatches(actualText, condition.Value),
            "not_in_list" => !AnyValueMatches(actualText, condition.Value),
            // Version comparison (SemVer.cs) reads the raw attribute: a number converts the way
            // JavaScript prints it, and any other type is no match. An SDK that predates these
            // operators reaches the discard arm, which is false.
            "semver_eq" or "semver_neq" or "semver_gt" or "semver_gte" or "semver_lt" or "semver_lte" =>
                SemVer.Evaluate(condition.Operator, actual, ExpectedScalar(condition.Value)),
            _ => false,
        };
    }

    /// <summary>
    /// Resolve a (contextKind, attribute) pair. With no declared kind the lookup falls back to
    /// the primary context, where "userId" still addresses its key for v1 datafiles.
    /// </summary>
    internal static object? ResolveAttribute(EvalContext ctx, string? contextKind, string attribute)
    {
        if (!string.IsNullOrEmpty(contextKind))
        {
            SingleContext? sub = ContextHelper.GetContextByKind(ctx, contextKind!);
            if (sub is null) return null;
            return ContextHelper.ReadSubContextAttribute(sub, attribute);
        }

        SingleContext? primary = ctx.IsMulti
            ? ContextHelper.GetContextByKind(ctx, "user")
            : ctx.Single;
        if (primary is null) return null;
        if (attribute == "userId") return primary.Key;
        return ContextHelper.ReadSubContextAttribute(primary, attribute);
    }

    private static object? ExpectedScalar(JsonElement value)
    {
        JsonElement scalar = value;
        if (value.ValueKind == JsonValueKind.Array)
        {
            scalar = default;
            foreach (JsonElement item in value.EnumerateArray())
            {
                scalar = item;
                break;
            }
        }
        return scalar.ValueKind switch
        {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => null,
            JsonValueKind.String => scalar.GetString(),
            JsonValueKind.Number => scalar.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => scalar.GetRawText(),
        };
    }

    private static bool EvalRegex(string actual, string pattern)
    {
        try
        {
            // A one-second cap: a pathological pattern in a datafile must not hang an
            // evaluation on the request path.
            return Regex.IsMatch(actual, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch
        {
            // The reference swallows an invalid pattern and reports no match.
            return false;
        }
    }

    private static bool AnyValueMatches(string actualText, JsonElement rawExpected)
    {
        if (rawExpected.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in rawExpected.EnumerateArray())
            {
                if (actualText == JsNumber.StringOf(ExpectedScalar(candidate))) return true;
            }
            return false;
        }
        return actualText == JsNumber.StringOf(ExpectedScalar(rawExpected));
    }
}
