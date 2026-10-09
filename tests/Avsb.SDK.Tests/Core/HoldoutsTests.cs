using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

public sealed class HoldoutsTests
{
    // Resolved once, by walking upward to the corpus (see ConformanceFixtures): a fixed count of
    // ".." segments from the build output landed one directory short.
    private static string FixturesRoot => ConformanceFixtures.Root;

    [Fact]
    public void EvaluateHoldout_ReturnsNotHeldOut_WhenNoHoldouts()
    {
        var df = new FlagDatafile { Flags = new(), Audiences = new() };
        var ctx = EvalContext.User("alice");
        var result = HoldoutEvaluator.EvaluateHoldout(ctx, "any-flag", df);
        result.HeldOut.Should().BeFalse();
    }

    [Fact]
    public void EvaluateHoldout_ReturnsNotHeldOut_WhenFlagNotInHoldout()
    {
        var df = new FlagDatafile
        {
            Flags = new(),
            Audiences = new(),
            Holdouts = new()
            {
                new FlagDatafileHoldout
                {
                    Id = "h1",
                    Key = "q2-holdout",
                    TrafficAllocation = 0.999,
                    FlagIds = new() { "other-flag" },
                    HashAttribute = "user.key",
                    DefaultVariationByFlag = new(),
                },
            },
        };
        var ctx = EvalContext.User("alice");
        var result = HoldoutEvaluator.EvaluateHoldout(ctx, "my-flag", df);
        result.HeldOut.Should().BeFalse();
    }

    [Fact]
    public void EvaluateHoldout_HoldsOutUser_WhenInsideTrafficWindow()
    {
        // Load the conformance datafile — the holdout has 99.9% allocation,
        // so effectively all users are held out.
        var df = InMemoryDatafileLoader.LoadFile(
            Path.Combine(FixturesRoot, "datafiles",
                "holdout_excludes_user_serves_holdout_variation.datafile.json"));
        var ctx = EvalContext.User("alice");
        var result = HoldoutEvaluator.EvaluateHoldout(ctx, "flag_holdout_target", df);
        result.HeldOut.Should().BeTrue();
        result.Holdout.Should().NotBeNull();
        result.VariationId.Should().Be("var_off");
    }

    [Fact]
    public void EvaluateHoldout_ZeroAllocation_NeverHoldsOut()
    {
        var df = new FlagDatafile
        {
            Flags = new(),
            Audiences = new(),
            Holdouts = new()
            {
                new FlagDatafileHoldout
                {
                    Id = "h2",
                    Key = "zero-holdout",
                    TrafficAllocation = 0,
                    FlagIds = new() { "flag_a" },
                    HashAttribute = "user.key",
                    DefaultVariationByFlag = new(),
                },
            },
        };
        var ctx = EvalContext.User("alice");
        var result = HoldoutEvaluator.EvaluateHoldout(ctx, "flag_a", df);
        result.HeldOut.Should().BeFalse();
    }

    [Fact]
    public void EvaluateHoldout_IsDeterministic_ForSameUser()
    {
        var df = InMemoryDatafileLoader.LoadFile(
            Path.Combine(FixturesRoot, "datafiles",
                "holdout_excludes_user_serves_holdout_variation.datafile.json"));
        var ctx = EvalContext.User("alice");
        var r1 = HoldoutEvaluator.EvaluateHoldout(ctx, "flag_holdout_target", df);
        var r2 = HoldoutEvaluator.EvaluateHoldout(ctx, "flag_holdout_target", df);
        r1.HeldOut.Should().Be(r2.HeldOut);
        r1.VariationId.Should().Be(r2.VariationId);
    }
}
