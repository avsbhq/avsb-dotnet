namespace Avsb.SDK.Core;

/// <summary>
/// Holdout + mutual-exclusion evaluation. Mirrors holdouts.ts §8.2.
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
    /// Walks the datafile's active holdouts; the first one that participates
    /// in flagId and whose bucket lands inside its traffic-allocation window
    /// classifies the context as held out.
    /// Bucketing: murmurhash3(holdout.key + ':' + bucketKey) % 10_000.
    /// </summary>
    public static HoldoutEvaluation EvaluateHoldout(
        EvalContext ctx,
        string flagId,
        FlagDatafile datafile)
    {
        if (datafile.Holdouts is null || datafile.Holdouts.Count == 0)
            return new HoldoutEvaluation { HeldOut = false };

        foreach (var holdout in datafile.Holdouts)
        {
            if (!holdout.FlagIds.Contains(flagId)) continue;
            if (holdout.TrafficAllocation <= 0) continue;

            var bucketKeyStr = ResolveBucketKey(ctx, holdout.HashAttribute);
            uint hash = Hash.Murmurhash3(holdout.Key + ":" + bucketKeyStr) % 10_000;
            uint threshold = (uint)Math.Floor(holdout.TrafficAllocation * 10_000);
            if (hash >= threshold) continue;

            holdout.DefaultVariationByFlag.TryGetValue(flagId, out var variationId);
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
        var val = ContextHelper.ResolveDottedAttribute(ctx, hashAttribute);
        return val?.ToString() ?? string.Empty;
    }
}
