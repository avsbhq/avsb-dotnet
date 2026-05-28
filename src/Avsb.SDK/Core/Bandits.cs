namespace Avsb.SDK.Core;

/// <summary>
/// Bandit evaluation engine — epsilon-greedy, Thompson sampling, UCB1.
/// Mirrors bandits.ts. All stochastic steps are seeded from murmurhash3
/// so every (banditKey, context) pair produces a stable decision.
/// </summary>
internal static class BanditEvaluator
{
    private const int MaxBucket = 10_000;
    private const long HashRange = 0x100000000L; // 2^32

    // -----------------------------------------------------------------------
    // Public model + store shapes.
    // -----------------------------------------------------------------------

    public sealed class PerActionStats
    {
        public double Mean { get; set; }
        public double Variance { get; set; } = 1;
        public int Samples { get; set; }
    }

    public sealed class BanditModel
    {
        public string Version { get; set; } = string.Empty;
        public string Algorithm { get; set; } = string.Empty;
        public Dictionary<string, PerActionStats> PerAction { get; set; } = new();
        public Dictionary<string, Dictionary<string, PerActionStats>>? ContextualModels { get; set; }
    }

    public interface IBanditModelStore
    {
        BanditModel? GetModel(string banditKey);
        void UpdateModel(string banditKey, BanditModel model);
    }

    // -----------------------------------------------------------------------
    // Seeded pseudo-randomness — Box-Muller from murmurhash3.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Standard-normal sample seeded deterministically from seed.
    /// Exported for tests so the seeded-random pipeline can be asserted.
    /// </summary>
    public static double SeededGaussian(uint seed)
    {
        double u1 = UniformFromSeed(seed, 1);
        double u2 = UniformFromSeed(seed, 2);
        // Box-Muller — z0 has mean 0, variance 1.
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static double UniformFromSeed(uint seed, int salt)
    {
        uint hashed = Hash.Murmurhash3($"{seed}:{salt}");
        return (hashed + 1.0) / (HashRange + 1.0);
    }

    // -----------------------------------------------------------------------
    // Algorithm: epsilon-greedy.
    // -----------------------------------------------------------------------

    public sealed class EpsilonGreedyResult
    {
        public BanditActionDef Action { get; init; } = null!;
        public double ActionProbability { get; init; }
        public bool ExploreFired { get; init; }
    }

    public static EpsilonGreedyResult EpsilonGreedy(
        Dictionary<string, PerActionStats> modelPerAction,
        List<BanditActionDef> actions,
        int hashBucket,
        double? explorationRate = null)
    {
        if (actions.Count == 0) throw new InvalidOperationException("EpsilonGreedy: actions list is empty");

        var sorted = actions.OrderBy(a => a.Id).ToList();
        double epsilon = explorationRate ?? 0.1;
        int n = sorted.Count;
        double bucketFraction = hashBucket / (double)MaxBucket;
        bool exploreFired = bucketFraction < epsilon;

        if (exploreFired)
        {
            var action = sorted[hashBucket % n];
            return new EpsilonGreedyResult
            {
                Action = action,
                ActionProbability = epsilon / n,
                ExploreFired = true,
            };
        }

        // Exploit — pick highest mean, alphabetical tie-break.
        string? bestId = null;
        double bestMean = double.NegativeInfinity;
        int bestCount = 0;
        foreach (var a in sorted)
        {
            var stats = StatsFor(modelPerAction, a.Id);
            if (stats.Mean > bestMean)
            {
                bestMean = stats.Mean;
                bestId = a.Id;
                bestCount = 1;
            }
            else if (stats.Mean == bestMean)
            {
                bestCount++;
            }
        }

        var best = sorted.First(a => a.Id == bestId);
        double exploitMass = (1 - epsilon) / bestCount;
        return new EpsilonGreedyResult
        {
            Action = best,
            ActionProbability = exploitMass + epsilon / n,
            ExploreFired = false,
        };
    }

    // -----------------------------------------------------------------------
    // Algorithm: Thompson sampling.
    // -----------------------------------------------------------------------

    public sealed class ThompsonResult
    {
        public BanditActionDef Action { get; init; } = null!;
        public double ActionProbability { get; init; }
    }

    public static ThompsonResult ThompsonSample(
        Dictionary<string, PerActionStats> modelPerAction,
        List<BanditActionDef> actions,
        int hashBucket)
    {
        if (actions.Count == 0) throw new InvalidOperationException("ThompsonSample: actions list is empty");

        var sorted = actions.OrderBy(a => a.Id).ToList();
        string bestId = sorted[0].Id;
        double bestSample = double.NegativeInfinity;

        foreach (var a in sorted)
        {
            var stats = StatsFor(modelPerAction, a.Id);
            uint seed = Hash.Murmurhash3($"{hashBucket}:{a.Id}");
            double z = SeededGaussian(seed);
            double stddev = Math.Sqrt(stats.Variance / Math.Max(stats.Samples, 1));
            double sample = stats.Mean + z * stddev;
            if (sample > bestSample)
            {
                bestSample = sample;
                bestId = a.Id;
            }
        }

        return new ThompsonResult
        {
            Action = sorted.First(a => a.Id == bestId),
            ActionProbability = 1.0 / sorted.Count,
        };
    }

    // -----------------------------------------------------------------------
    // Algorithm: UCB1.
    // -----------------------------------------------------------------------

    public sealed class Ucb1Result
    {
        public BanditActionDef Action { get; init; } = null!;
        public double ActionProbability { get; init; }
    }

    public static Ucb1Result Ucb1(
        Dictionary<string, PerActionStats> modelPerAction,
        List<BanditActionDef> actions)
    {
        if (actions.Count == 0) throw new InvalidOperationException("UCB1: actions list is empty");

        var sorted = actions.OrderBy(a => a.Id).ToList();

        // Cold-start: sample every action at least once.
        foreach (var a in sorted)
        {
            if (StatsFor(modelPerAction, a.Id).Samples == 0)
                return new Ucb1Result { Action = a, ActionProbability = 1.0 };
        }

        int totalSamples = sorted.Sum(a => StatsFor(modelPerAction, a.Id).Samples);
        double lnTotal = Math.Log(Math.Max(totalSamples, 1));

        string bestId = sorted[0].Id;
        double bestScore = double.NegativeInfinity;
        foreach (var a in sorted)
        {
            var stats = StatsFor(modelPerAction, a.Id);
            double score = stats.Mean + Math.Sqrt(2 * lnTotal / Math.Max(stats.Samples, 1));
            if (score > bestScore)
            {
                bestScore = score;
                bestId = a.Id;
            }
        }

        return new Ucb1Result
        {
            Action = sorted.First(a => a.Id == bestId),
            ActionProbability = 1.0,
        };
    }

    // -----------------------------------------------------------------------
    // Top-level entry point.
    // -----------------------------------------------------------------------

    public sealed class BanditDecision
    {
        public BanditActionDef Action { get; init; } = null!;
        public double ActionProbability { get; init; }
        public double? OptimalityGap { get; init; }
        public string ModelVersion { get; init; } = string.Empty;
    }

    public static BanditDecision? EvaluateBandit(
        string banditKey,
        BanditConfig config,
        EvalContext ctx,
        IBanditModelStore store)
    {
        var model = store.GetModel(banditKey);
        if (model is null) return null;

        string bucketKey = DeriveContextBucket(config, ctx);
        var perActionMap = (bucketKey.Length > 0 && model.ContextualModels?.ContainsKey(bucketKey) == true)
            ? model.ContextualModels[bucketKey]
            : model.PerAction;

        int hashBucket = (int)(Hash.Murmurhash3($"{banditKey}:{ContextHelper.BucketingKey(ctx)}") % MaxBucket);

        BanditActionDef action;
        double actionProbability;
        double? optimalityGap;

        switch (config.Algorithm)
        {
            case "epsilon-greedy":
            {
                var r = EpsilonGreedy(perActionMap, config.Actions, hashBucket, config.ExplorationRate);
                action = r.Action;
                actionProbability = r.ActionProbability;
                optimalityGap = r.ExploreFired ? (config.ExplorationRate ?? 0.1) : null;
                break;
            }
            case "thompson-sampling":
            {
                var r = ThompsonSample(perActionMap, config.Actions, hashBucket);
                action = r.Action;
                actionProbability = r.ActionProbability;
                optimalityGap = null;
                break;
            }
            case "ucb1":
            {
                var r = Ucb1(perActionMap, config.Actions);
                action = r.Action;
                actionProbability = r.ActionProbability;
                optimalityGap = null;
                break;
            }
            default:
                throw new InvalidOperationException($"EvaluateBandit: unknown algorithm {config.Algorithm}");
        }

        return new BanditDecision
        {
            Action = action,
            ActionProbability = actionProbability,
            OptimalityGap = optimalityGap,
            ModelVersion = model.Version,
        };
    }

    // -----------------------------------------------------------------------
    // Helpers.
    // -----------------------------------------------------------------------

    private static PerActionStats StatsFor(Dictionary<string, PerActionStats> perAction, string actionId)
    {
        if (perAction.TryGetValue(actionId, out var stats)) return stats;
        return new PerActionStats { Mean = 0, Variance = 1, Samples = 0 };
    }

    private static string DeriveContextBucket(BanditConfig config, EvalContext ctx)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in config.Actions)
        {
            if (a.ContextAttributes is null) continue;
            foreach (var k in a.ContextAttributes.Keys) keys.Add(k);
        }
        if (keys.Count == 0) return string.Empty;

        var parts = keys.Select(k =>
        {
            var val = ResolveContextAttribute(ctx, k);
            return $"{k}={val}";
        });
        return string.Join(";", parts);
    }

    private static string ResolveContextAttribute(EvalContext ctx, string key)
    {
        var val = ContextHelper.ResolveDottedAttribute(ctx, key);
        return val?.ToString() ?? string.Empty;
    }
}
