namespace Avsb.SDK.Core;

/// <summary>
/// Holdout and mutual-exclusion evaluation. Mirrors holdouts.ts.
/// </summary>
internal static class HoldoutEvaluator
{
    public readonly struct HoldoutEvaluation
    {
        public bool HeldOut { get; init; }
        public FlagDatafileHoldout? Holdout { get; init; }
        public string? VariationId { get; init; }
    }

    /// <summary>
    /// Walks the datafile's holdouts; the first one that participates in flagId and whose bucket
    /// lands inside its traffic-allocation window classifies the context as held out for THIS
    /// flag. Bucketing is murmurhash3(holdout.key + ':' + bucketKey) % 10_000, so several flags
    /// sharing a holdout classify a context consistently. That is the mutual-exclusion guarantee.
    /// </summary>
    public static HoldoutEvaluation EvaluateHoldout(
        EvalContext ctx,
        string flagId,
        FlagDatafile datafile)
    {
        if (datafile.Holdouts is null || datafile.Holdouts.Count == 0)
            return new HoldoutEvaluation { HeldOut = false };

        foreach (FlagDatafileHoldout holdout in datafile.Holdouts)
        {
            if (!holdout.FlagIds.Contains(flagId)) continue;
            if (holdout.TrafficAllocation <= 0) continue;

            string bucketKeyStr = ResolveBucketKey(ctx, holdout.HashAttribute);
            uint hash = Hash.Murmurhash3(holdout.Key + ":" + bucketKeyStr) % 10_000;
            // Float comparison against the raw product, never a floor (spec section 1.4).
            // Flooring put holdouts one bucket out of step with the ab_test allocation gate: a
            // 0.12345 holdout has a threshold of 1234.5, so bucket 1234 is inside the window,
            // but the floored form compared against 1234 and let that context through.
            double threshold = holdout.TrafficAllocation * 10_000;
            if (hash >= threshold) continue;

            holdout.DefaultVariationByFlag.TryGetValue(flagId, out string? variationId);
            return new HoldoutEvaluation
            {
                HeldOut = true,
                Holdout = holdout,
                VariationId = variationId,
            };
        }

        return new HoldoutEvaluation { HeldOut = false };
    }

    private static string ResolveBucketKey(EvalContext ctx, string hashAttribute)
    {
        object? val = ContextHelper.ResolveDottedAttribute(ctx, hashAttribute);
        // JavaScript coercion, not .NET's: a numeric hash attribute must stringify to "42", not
        // "42.0" and not a culture-formatted "42,0", or this process buckets the user
        // differently from every other SDK.
        return val is null ? string.Empty : JsNumber.StringOf(val);
    }
}
