using Avsb.SDK.Core;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

public sealed class BanditsTests
{
    private static List<BanditActionDef> TwoActions() =>
        new()
        {
            new BanditActionDef { Id = "action-a", VariationId = "var-a" },
            new BanditActionDef { Id = "action-b", VariationId = "var-b" },
        };

    private static Dictionary<string, BanditEvaluator.PerActionStats> ModelWithHighMean() =>
        new()
        {
            ["action-a"] = new BanditEvaluator.PerActionStats { Mean = 0.9, Variance = 0.1, Samples = 100 },
            ["action-b"] = new BanditEvaluator.PerActionStats { Mean = 0.2, Variance = 0.1, Samples = 100 },
        };

    [Fact]
    public void EpsilonGreedy_Exploit_PicksHighestMean()
    {
        var result = BanditEvaluator.EpsilonGreedy(
            ModelWithHighMean(), TwoActions(),
            hashBucket: 5000, // > 0.1 * 10000 → exploit branch
            explorationRate: 0.1);

        result.Action.Id.Should().Be("action-a");
        result.ExploreFired.Should().BeFalse();
    }

    [Fact]
    public void EpsilonGreedy_Explore_PicksByBucketIndex()
    {
        var result = BanditEvaluator.EpsilonGreedy(
            ModelWithHighMean(), TwoActions(),
            hashBucket: 0, // 0 < 0.1 * 10000 → explore
            explorationRate: 0.1);

        result.ExploreFired.Should().BeTrue();
        // bucket=0, n=2 → index 0 % 2 = 0 → action-a (sorted)
        result.Action.Id.Should().Be("action-a");
    }

    [Fact]
    public void ThompsonSample_Deterministic_ForSameSeed()
    {
        var r1 = BanditEvaluator.ThompsonSample(ModelWithHighMean(), TwoActions(), hashBucket: 999);
        var r2 = BanditEvaluator.ThompsonSample(ModelWithHighMean(), TwoActions(), hashBucket: 999);
        r1.Action.Id.Should().Be(r2.Action.Id);
    }

    [Fact]
    public void Ucb1_ColdStart_PicksFirstUnseen()
    {
        var emptyModel = new Dictionary<string, BanditEvaluator.PerActionStats>();
        var result = BanditEvaluator.Ucb1(emptyModel, TwoActions());
        // Alphabetically first unseen action should be picked.
        result.Action.Id.Should().Be("action-a");
        result.ActionProbability.Should().Be(1.0);
    }

    [Fact]
    public void SeededGaussian_ReturnsDifferentValuesForDifferentSeeds()
    {
        double g1 = BanditEvaluator.SeededGaussian(1);
        double g2 = BanditEvaluator.SeededGaussian(2);
        g1.Should().NotBe(g2);
    }

    [Fact]
    public void SeededGaussian_ReturnsSameValue_ForSameSeed()
    {
        double g1 = BanditEvaluator.SeededGaussian(42);
        double g2 = BanditEvaluator.SeededGaussian(42);
        g1.Should().Be(g2);
    }
}
