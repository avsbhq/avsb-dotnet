using System.Text.Json;

namespace Avsb.SDK.Core;

/// <summary>
/// Commerce audience conditions, ported from audienceCommerce.ts.
///
/// Three condition families, all fed from caller-supplied context attributes:
/// CART_VALUE reads cartValue (a decimal number in major units), PRODUCTS_VIEWED reads
/// viewedSkus or viewedCategories, and PURCHASE_HISTORY reads the purchaseHistory object whose
/// hasPurchased boolean is mandatory for any operator to answer.
///
/// Two rules carried over verbatim because they are deliberate product decisions:
///
/// 1. Money on the CONDITION is encoded in MINOR units and converted with the datafile's
///    currencyExponent (absent means 2). A zero-exponent currency such as JPY publishes 0, so
///    "25000" means 25000, not 250.00.
/// 2. Every "cannot know" path is false: a missing or mistyped attribute, a non-numeric
///    condition value, dataset-backed membership, or an unknown operator. That includes
///    has_not_purchased against an absent purchaseHistory: server callers own their data, so
///    absence means "not provided", not "never purchased".
///
/// Nothing in this class throws.
/// </summary>
internal static class AudienceCommerce
{
    /// <summary>C5: currencies publish their own exponent; absent means the 2-decimal default.</summary>
    public const int DefaultCurrencyExponent = 2;

    private const long MsPerDay = 86_400_000L;

    /// <summary>Normalise the datafile field, treating a negative or absent exponent as the default.</summary>
    public static int CurrencyExponentOf(int? datafileExponent) =>
        datafileExponent is null || datafileExponent < 0 ? DefaultCurrencyExponent : datafileExponent.Value;

    /// <summary>Evaluate one commerce condition.</summary>
    public static bool EvaluateCommerceCondition(
        Condition condition,
        Func<string, object?> resolveAttribute,
        int currencyExponent) => condition.Type switch
        {
            "CART_VALUE" => EvalCartValue(condition, resolveAttribute, currencyExponent),
            "PRODUCTS_VIEWED" => EvalProductsViewed(condition, resolveAttribute),
            "PURCHASE_HISTORY" => EvalPurchaseHistory(condition, resolveAttribute, currencyExponent),
            _ => false,
        };

    // -----------------------------------------------------------------------
    // CART_VALUE
    // -----------------------------------------------------------------------

    private static bool EvalCartValue(
        Condition condition, Func<string, object?> resolveAttribute, int exponent)
    {
        // cartValue is a decimal number in major units. A string, a null, or an object is
        // mistyped and fails closed rather than being coerced into a match.
        double? cart = FiniteNumber(resolveAttribute("cartValue"));
        if (cart is null) return false;

        if (condition.Operator == "between")
        {
            IReadOnlyList<JsonElement>? bounds = AsArray(condition.Value);
            if (bounds is null || bounds.Count < 2) return false;
            double? lower = ParseMinorToMajor(bounds[0], exponent);
            double? upper = ParseMinorToMajor(bounds[1], exponent);
            if (lower is null || upper is null) return false;
            // C1: between is inclusive on both ends.
            return cart >= lower && cart <= upper;
        }

        double? threshold = ParseMinorToMajor(SingleValue(condition), exponent);
        if (threshold is null) return false;

        return condition.Operator switch
        {
            "greater_than" => cart > threshold,
            "gte" => cart >= threshold,
            "less_than" => cart < threshold,
            "lte" => cart <= threshold,
            _ => false,
        };
    }

    // -----------------------------------------------------------------------
    // PRODUCTS_VIEWED
    // -----------------------------------------------------------------------

    private static bool EvalProductsViewed(Condition condition, Func<string, object?> resolveAttribute)
    {
        // Dataset-backed membership is resolved from browser-side dataset windows, so the
        // server cannot answer it. Both operators degrade to false: matching all traffic would
        // be worse than matching none.
        if (!string.IsNullOrEmpty(condition.DatasetSlug)) return false;

        string attributeName = condition.Match == "category" ? "viewedCategories" : "viewedSkus";
        if (resolveAttribute(attributeName) is not IReadOnlyList<object?> actual) return false;

        var viewed = new HashSet<string>(StringComparer.Ordinal);
        foreach (object? item in actual)
        {
            if (item is string s) viewed.Add(s);
        }

        IReadOnlyList<JsonElement> candidates = AsArray(condition.Value)
            ?? new[] { condition.Value };

        bool anyTarget = false;
        bool anyViewed = false;
        foreach (JsonElement candidate in candidates)
        {
            if (candidate.ValueKind != JsonValueKind.String) continue;
            string? target = candidate.GetString();
            if (string.IsNullOrEmpty(target)) continue;
            anyTarget = true;
            if (viewed.Contains(target!)) anyViewed = true;
        }
        // No membership source means neither operator can answer (C1 asymmetry rule).
        if (!anyTarget) return false;

        return condition.Operator switch
        {
            "viewed_any" => anyViewed,
            "not_viewed_any" => !anyViewed,
            _ => false,
        };
    }

    // -----------------------------------------------------------------------
    // PURCHASE_HISTORY
    // -----------------------------------------------------------------------

    private static bool EvalPurchaseHistory(
        Condition condition, Func<string, object?> resolveAttribute, int exponent)
    {
        if (resolveAttribute("purchaseHistory") is not IReadOnlyDictionary<string, object?> profile)
        {
            return false;
        }
        if (!profile.TryGetValue("hasPurchased", out object? flag) || flag is not bool hasPurchased)
        {
            return false;
        }

        switch (condition.Operator)
        {
            case "has_purchased":
                return hasPurchased;
            case "has_not_purchased":
                return !hasPurchased;
            case "last_purchase_older_than":
            {
                double? days = ParseNumeric(SingleValue(condition));
                if (days is null) return false;
                double? lastPurchaseAt = profile.TryGetValue("lastPurchaseAt", out object? at)
                    ? FiniteNumber(at)
                    : null;
                if (lastPurchaseAt is not null)
                {
                    return lastPurchaseAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        - days.Value * MsPerDay;
                }
                // No usable timestamp: vacuously older only for a visitor who never purchased.
                // A purchaser with an unknown date is a "cannot know".
                return !hasPurchased;
            }
            case "lifetime_spend_gt":
            {
                double? threshold = ParseMinorToMajor(SingleValue(condition), exponent);
                double? spend = profile.TryGetValue("lifetimeSpend", out object? s)
                    ? FiniteNumber(s)
                    : null;
                return threshold is not null && spend is not null && spend > threshold;
            }
            case "order_count_gt":
            {
                double? minimum = ParseNumeric(SingleValue(condition));
                double? count = profile.TryGetValue("orderCount", out object? c)
                    ? FiniteNumber(c)
                    : null;
                return minimum is not null && count is not null && count > minimum;
            }
            default:
                return false;
        }
    }

    // -----------------------------------------------------------------------
    // Value helpers
    // -----------------------------------------------------------------------

    private static IReadOnlyList<JsonElement>? AsArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var items = new List<JsonElement>();
        foreach (JsonElement item in value.EnumerateArray()) items.Add(item);
        return items;
    }

    private static JsonElement SingleValue(Condition condition)
    {
        IReadOnlyList<JsonElement>? list = AsArray(condition.Value);
        if (list is null) return condition.Value;
        return list.Count == 0 ? default : list[0];
    }

    /// <summary>
    /// Parse a minor-units integer string (C1 money encoding) into decimal major units. Null for
    /// anything that is not a numeric string, which the caller treats as false.
    /// </summary>
    private static double? ParseMinorToMajor(JsonElement raw, int exponent)
    {
        if (raw.ValueKind != JsonValueKind.String) return null;
        string? text = raw.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        double minor = JsNumber.NumberOf(text);
        if (!double.IsFinite(minor)) return null;
        return minor / Math.Pow(10, exponent);
    }

    /// <summary>Parse a plain numeric string condition value (days, counts).</summary>
    private static double? ParseNumeric(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.String) return null;
        string? text = raw.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        double parsed = JsNumber.NumberOf(text);
        return double.IsFinite(parsed) ? parsed : null;
    }

    /// <summary>A finite numeric attribute value, or null when absent or mistyped.</summary>
    private static double? FiniteNumber(object? raw) => raw switch
    {
        double d when double.IsFinite(d) => d,
        float f when float.IsFinite(f) => f,
        int i => i,
        long l => l,
        short s => s,
        decimal dec => (double)dec,
        _ => null,
    };
}
