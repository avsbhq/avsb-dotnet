namespace Avsb.SDK.Core;

public static partial class Evaluator
{
    /// <summary>
    /// The traffic-allocation gate, shared by ab_test and targeted_delivery rules. Mirrors
    /// allocationExclusion in evaluate.ts. Returns the exclusion reason when the context falls
    /// outside the rule's rollout, or null when it is inside (or the rule declares no gate).
    ///
    /// One hash input for both rule types, hashValue + flagKey + "__" + ruleId, so a visitor's
    /// rollout membership is the same in every SDK and does not move when a rule changes type.
    ///
    /// The threshold is a FLOAT comparison against the raw product, never a truncated or rounded
    /// integer (spec section 1.4). A 0.12345 rollout puts the threshold at 1234.5: bucket 1234 is
    /// inside it, while a (uint) cast, which this SDK once had, excluded that context and diverged
    /// by one bucket in ten thousand, per rollout, undetectably.
    ///
    /// A null allocation means no gate, and so does a field missing from an old datafile (it
    /// deserialises to null).
    /// </summary>
    private static string? AllocationExclusion(FlagDatafileRule rule, string hashValue, string flagKey)
    {
        if (rule.TrafficAllocation is null) return null;
        uint allocHash = Hash.Murmurhash3(hashValue + flagKey + "__" + rule.Id) % 10_000;
        double threshold = rule.TrafficAllocation.Value * 10_000;
        // Written as the reference writes it (">=" excludes), so a NaN threshold, which JSON cannot
        // carry but a hand-built datafile could, serves everyone exactly as it does there.
        return allocHash >= threshold
            ? $"rule \"{rule.Id}\" excluded by traffic allocation (hash {allocHash} >= threshold"
                + $" {JsNumber.Stringify(threshold)})"
            : null;
    }
}
