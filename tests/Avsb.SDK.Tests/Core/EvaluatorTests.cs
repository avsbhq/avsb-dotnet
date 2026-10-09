using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

/// <summary>
/// Note on value assertions: EvalResult.Value carries the datafile's variation value as a
/// JsonElement whenever a variation was served, and the caller's own CLR default only when the
/// datafile could not answer. Keeping the JsonElement is what lets GetJsonFlag&lt;T&gt;
/// deserialise directly instead of round-tripping through a string, so these assertions unwrap it
/// by JSON kind rather than comparing a boxed object.
/// </summary>
public sealed class EvaluatorTests
{
    // Resolved once, by walking upward to the corpus (see ConformanceFixtures): a fixed count of
    // ".." segments from the build output landed one directory short.
    private static string FixturesRoot => ConformanceFixtures.Root;

    private static FlagDatafile LoadDatafile(string name) =>
        InMemoryDatafileLoader.LoadFile(Path.Combine(FixturesRoot, "datafiles", $"{name}.datafile.json"));

    [Fact]
    public void FlagNotFound_Returns_NotFoundSource()
    {
        var df = LoadDatafile("flag_not_found_returns_default");
        var ctx = EvalContext.User("alice");
        var result = Evaluator.EvaluateFlag("does-not-exist", false, ctx, df);
        result.Source.Should().Be(EvaluationSource.NotFound);
        // No variation was served, so this is the caller's own CLR default, not a JsonElement.
        result.Value.Should().Be(false);
        result.VariationKey.Should().BeNull();
    }

    [Fact]
    public void DisabledFlag_Returns_DefaultVariationWithDisabledSource()
    {
        // Fixture: feature-off is disabled; default variation is "control" (value=false)
        var df = LoadDatafile("disabled_flag_returns_default_variation_value");
        var ctx = EvalContext.User("any_user");
        var result = Evaluator.EvaluateFlag("feature-off", true, ctx, df);
        result.Source.Should().Be(EvaluationSource.Disabled);
        ((JsonElement)result.Value!).GetBoolean().Should().BeFalse();
        result.VariationKey.Should().Be("control");
    }

    [Fact]
    public void UserOverride_Wins_OverRules()
    {
        // user_42 has an explicit override in the fixture
        var df = LoadDatafile("user_override_wins_over_rules");
        var ctx = EvalContext.User("user_42");
        var result = Evaluator.EvaluateFlag("feature-overridden", "caller-default", ctx, df);
        result.Source.Should().Be(EvaluationSource.DatafileOverride);
        ((JsonElement)result.Value!).GetString().Should().Be("forced-on");
        result.VariationKey.Should().Be("forced");
    }

    [Fact]
    public void TargetedDelivery_PaidUser_Matches()
    {
        // Fixture context: user_pro_1 with plan=pro
        var df = LoadDatafile("targeted_delivery_paid_user_matches");
        var ctx = EvalContext.User("user_pro_1",
            attributes: new Dictionary<string, object?> { ["plan"] = "pro" });
        var result = Evaluator.EvaluateFlag("paid-only-feature", false, ctx, df);
        result.Source.Should().Be(EvaluationSource.Rule);
        result.RuleType.Should().Be(RuleType.TargetedDelivery);
        ((JsonElement)result.Value!).GetBoolean().Should().BeTrue();
        result.VariationKey.Should().Be("on");
    }

    [Fact]
    public void TargetedDelivery_FreeUser_FallsToDefault()
    {
        // Fixture context: user_free_1 with plan=free; misses paid audience
        var df = LoadDatafile("targeted_delivery_free_user_misses_falls_to_default");
        var ctx = EvalContext.User("user_free_1",
            attributes: new Dictionary<string, object?> { ["plan"] = "free" });
        var result = Evaluator.EvaluateFlag("paid-only-feature", false, ctx, df);
        result.Source.Should().Be(EvaluationSource.Default);
        // The flag's own default variation was served, so this is that variation's value.
        ((JsonElement)result.Value!).GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void AbTest_Deterministic_UserA_MatchesExpected()
    {
        var df = LoadDatafile("ab_test_bucket_deterministic_user_a");
        var ctx = EvalContext.User("alice");
        var result = Evaluator.EvaluateFlag("checkout-v2-experiment", "fallback", ctx, df);
        result.Source.Should().Be(EvaluationSource.Rule);
        result.RuleType.Should().Be(RuleType.AbTest);
        result.VariationKey.Should().Be("control");
        ((JsonElement)result.Value!).GetString().Should().Be("old");
    }

    [Fact]
    public void AbTest_Deterministic_UserB_MatchesExpected()
    {
        var df = LoadDatafile("ab_test_bucket_deterministic_user_b");
        var ctx = EvalContext.User("bob");
        var result = Evaluator.EvaluateFlag("checkout-v2-experiment", "fallback", ctx, df);
        result.Source.Should().Be(EvaluationSource.Rule);
        result.RuleType.Should().Be(RuleType.AbTest);
        result.VariationKey.Should().NotBeNull();
    }

    [Fact]
    public void Holdout_ExcludesUser_ServesHoldoutVariation()
    {
        var df = LoadDatafile("holdout_excludes_user_serves_holdout_variation");
        var ctx = EvalContext.User("alice");
        var result = Evaluator.EvaluateFlag("feature-with-holdout", false, ctx, df);
        result.Source.Should().Be(EvaluationSource.Holdout);
        result.RuleType.Should().Be(RuleType.Holdout);
        ((JsonElement)result.Value!).GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void MultiContext_EnterpriseOrg_Matches()
    {
        var df = LoadDatafile("multi_context_enterprise_org_matches");
        var ctx = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["user"] = new SingleContext { Kind = "user", Key = "alice" },
                ["organization"] = new SingleContext { Kind = "organization", Key = "acme",
                    Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" } },
            },
        });
        var result = Evaluator.EvaluateFlag("org-enterprise-feature", false, ctx, df);
        result.Source.Should().Be(EvaluationSource.Rule);
        ((JsonElement)result.Value!).GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void MultiContext_StartupOrg_MissesRule_FallsToDefault()
    {
        var df = LoadDatafile("multi_context_startup_org_misses");
        var ctx = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["user"] = new SingleContext { Kind = "user", Key = "alice" },
                ["organization"] = new SingleContext { Kind = "organization", Key = "startup-co",
                    Attributes = new Dictionary<string, object?> { ["tier"] = "startup" } },
            },
        });
        var result = Evaluator.EvaluateFlag("org-enterprise-feature", false, ctx, df);
        result.Source.Should().Be(EvaluationSource.Default);
        // The flag's own default variation was served, so this is that variation's value.
        ((JsonElement)result.Value!).GetBoolean().Should().BeFalse();
    }
}
