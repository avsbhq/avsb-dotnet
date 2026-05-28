using System.Text.Json;
using System.Text.RegularExpressions;

namespace Avsb.SDK.Core;

/// <summary>
/// Server-side audience evaluation. Mirrors audience.ts — only
/// CUSTOM_ATTRIBUTE conditions are evaluable server-side; browser-detected
/// types (DEVICE, BROWSER, PLATFORM, LANGUAGE, COOKIE, QUERY_PARAM) always
/// return false on the server.
/// </summary>
internal static class AudienceEvaluator
{
    public static bool EvaluateAudience(FlagDatafileAudience audience, EvalContext ctx)
    {
        if (audience.Rules is null) return true;
        return EvaluateGroup(audience.Rules, ctx);
    }

    public static bool EvaluateGroup(ConditionGroup group, EvalContext ctx)
    {
        if (group.Conditions.Count == 0) return true;

        bool isAnd = string.Equals(group.Logic, "AND", StringComparison.OrdinalIgnoreCase);

        foreach (var element in group.Conditions)
        {
            bool result = EvaluateElement(element, ctx);
            if (isAnd && !result) return false;
            if (!isAnd && result) return true;
        }

        return isAnd;
    }

    private static bool EvaluateElement(JsonElement element, EvalContext ctx)
    {
        // Distinguish ConditionGroup (has "logic") from Condition.
        if (element.TryGetProperty("logic", out _))
        {
            var nested = element.Deserialize<ConditionGroup>(JsonOptions.Default);
            return nested is not null && EvaluateGroup(nested, ctx);
        }

        var condition = element.Deserialize<Condition>(JsonOptions.Default);
        return condition is not null && EvaluateCondition(condition, ctx);
    }

    public static bool EvaluateCondition(Condition condition, EvalContext ctx)
    {
        return condition.Type switch
        {
            "CUSTOM_ATTRIBUTE" => EvalCustomAttribute(condition, ctx),
            // Browser-detected types are not evaluable server-side.
            "DEVICE" or "BROWSER" or "PLATFORM" or "LANGUAGE" or "COOKIE" or "QUERY_PARAM" => false,
            _ => false,
        };
    }

    private static bool EvalCustomAttribute(Condition condition, EvalContext ctx)
    {
        if (string.IsNullOrEmpty(condition.Attribute)) return false;

        // When contextKind is declared, the sub-context must be present.
        if (condition.ContextKind is not null)
        {
            var sub = ContextHelper.GetContextByKind(ctx, condition.ContextKind);
            if (sub is null) return false;
        }

        var actual = condition.ContextKind is not null
            ? GetAttributeForKind(ctx, condition.ContextKind, condition.Attribute)
            : GetAttributeFallback(ctx, condition.Attribute);

        string expected = GetExpectedScalar(condition.Value);

        return condition.Operator switch
        {
            "exists" => actual is not null,
            "not_exists" => actual is null,
            "equals" => StringOf(actual) == expected,
            "not_equals" => StringOf(actual) != expected,
            "contains" => StringOf(actual).Contains(expected, StringComparison.OrdinalIgnoreCase),
            "not_contains" => !StringOf(actual).Contains(expected, StringComparison.OrdinalIgnoreCase),
            "starts_with" => StringOf(actual).StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "ends_with" => StringOf(actual).EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "regex" => EvalRegex(StringOf(actual), expected),
            "greater_than" => ToDouble(actual) > ToDouble(expected),
            "less_than" => ToDouble(actual) < ToDouble(expected),
            "gte" => ToDouble(actual) >= ToDouble(expected),
            "lte" => ToDouble(actual) <= ToDouble(expected),
            "in_list" => InList(actual, condition.Value, negate: false),
            "not_in_list" => InList(actual, condition.Value, negate: true),
            _ => false,
        };
    }

    private static object? GetAttributeForKind(EvalContext ctx, string kind, string attribute)
    {
        var sub = ContextHelper.GetContextByKind(ctx, kind);
        if (sub is null) return null;
        return ContextHelper.ReadSubContextAttribute(sub, attribute);
    }

    private static object? GetAttributeFallback(EvalContext ctx, string attribute)
    {
        // No contextKind on the condition — fall back to primary context.
        if (!ctx.IsMulti)
        {
            if (attribute == "userId") return ctx.Single!.Key;
            return ContextHelper.ReadSubContextAttribute(ctx.Single!, attribute);
        }
        var user = ContextHelper.GetContextByKind(ctx, "user");
        if (user is null) return null;
        if (attribute == "userId") return user.Key;
        return ContextHelper.ReadSubContextAttribute(user, attribute);
    }

    private static string GetExpectedScalar(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
            return value[0].ToString();
        return value.ToString();
    }

    private static string StringOf(object? val) =>
        val?.ToString() ?? string.Empty;

    private static double ToDouble(object? val)
    {
        if (val is null) return double.NaN;
        if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return double.NaN;
    }

    private static double ToDouble(string s)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return double.NaN;
    }

    private static bool EvalRegex(string actual, string pattern)
    {
        try
        {
            return Regex.IsMatch(actual, pattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool InList(object? actual, JsonElement valueElement, bool negate)
    {
        var list = valueElement.ValueKind == JsonValueKind.Array
            ? valueElement.EnumerateArray().Select(e => e.ToString()).ToList()
            : new List<string> { valueElement.ToString() };

        var str = StringOf(actual);
        bool inList = list.Any(v => str == v);
        return negate ? !inList : inList;
    }
}
