using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

/// <summary>
/// Targeted delivery rollouts (N1-1, production QA 2026-10-01).
///
/// A TARGETED_DELIVERY rule with trafficAllocation 0.3 used to serve 100 percent of its audience in
/// every SDK, because the evaluator only applied the allocation to ab_test rules. These tests lock
/// the fix: the same gate, hash input and float threshold as an ab_test rule. They mirror
/// packages/avsb-core/src/evaluate.rollout.test.ts, and the exact counts were computed by the
/// TypeScript reference (@avsbhq/core 1.6.0) over the same keys.
/// </summary>
public sealed class EvaluatorRolloutTests
{
    private const string FlagKey = "rollout_feature";
    private const string RuleId = "rule_rollout";
    private const int Users = 10_000;

    private static Dictionary<string, object?> TdRule(double? trafficAllocation,
        string id = RuleId, string variationId = "var_on", string type = "targeted_delivery",
        string[]? audienceIds = null, bool noVariations = false) => new()
    {
        ["id"] = id,
        ["type"] = type,
        ["enabled"] = true,
        ["audienceIds"] = audienceIds ?? Array.Empty<string>(),
        ["hashAttribute"] = "user.key",
        ["trafficAllocation"] = trafficAllocation,
        ["variations"] = noVariations
            ? Array.Empty<object>()
            : new object[] { new Dictionary<string, object?> { ["variationId"] = variationId, ["percentage"] = 1 } },
    };

    private static FlagDatafile DatafileWith(object[] rules, object[]? audiences = null)
    {
        var datafile = new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["projectType"] = "FEATURE_FLAG",
            ["sdkKey"] = "sdk_test_rolloutfixture00001",
            ["environmentKey"] = "test",
            ["publishedAt"] = "2026-10-01T00:00:00.000Z",
            ["collectEndpoint"] = "https://collect.test.invalid",
            ["flags"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "flag_rollout",
                    ["key"] = FlagKey,
                    ["type"] = "boolean",
                    ["enabled"] = true,
                    ["defaultVariationId"] = "var_off",
                    ["variations"] = new object[]
                    {
                        new Dictionary<string, object?> { ["id"] = "var_off", ["key"] = "off", ["value"] = false },
                        new Dictionary<string, object?> { ["id"] = "var_on", ["key"] = "on", ["value"] = true },
                        new Dictionary<string, object?> { ["id"] = "var_fallback", ["key"] = "fallback", ["value"] = true },
                    },
                    ["overrides"] = Array.Empty<object>(),
                    ["rules"] = rules,
                },
            },
            ["audiences"] = audiences ?? Array.Empty<object>(),
        };
        return InMemoryDatafileLoader.Load(JsonSerializer.Serialize(datafile));
    }

    private static string UserKey(int i) => $"user-{i}";

    private static int ServedCount(FlagDatafile datafile, int count = Users)
    {
        int served = 0;
        for (int i = 0; i < count; i++)
        {
            if (Evaluator.EvaluateFlag(FlagKey, false, EvalContext.User(UserKey(i)), datafile).RuleId == RuleId)
            {
                served++;
            }
        }
        return served;
    }

    private static uint AllocationBucket(string key) =>
        Hash.Murmurhash3(key + FlagKey + "__" + RuleId) % 10_000;

    private static string KeyForAllocationBucket(uint bucket, string prefix)
    {
        for (int i = 0; i < 200_000; i++)
        {
            string candidate = $"{prefix}-{i}";
            if (AllocationBucket(candidate) == bucket) return candidate;
        }
        throw new InvalidOperationException($"no key hashed to allocation bucket {bucket}");
    }

    // -----------------------------------------------------------------------
    // Rollout share.
    // -----------------------------------------------------------------------

    [Fact]
    public void AThirtyPercentRule_ServesAboutThirtyPercentOf10000Users()
    {
        // The TypeScript reference serves exactly 2,984 of these keys.
        ServedCount(DatafileWith(new object[] { TdRule(0.3) })).Should().Be(2984);
    }

    [Fact]
    public void TheFiveAndFiftyPercentRollouts_MatchTheReferenceCounts()
    {
        ServedCount(DatafileWith(new object[] { TdRule(0.05) })).Should().Be(536);
        ServedCount(DatafileWith(new object[] { TdRule(0.5) })).Should().Be(4939);
    }

    [Fact]
    public void ANullAllocation_ServesEveryone_ExactlyAsBeforeTheFix()
    {
        ServedCount(DatafileWith(new object[] { TdRule(null) }), 2000).Should().Be(2000);
    }

    [Fact]
    public void AFullAllocationServesEveryone_AndAZeroAllocationNoOne()
    {
        ServedCount(DatafileWith(new object[] { TdRule(1) }), 2000).Should().Be(2000);
        ServedCount(DatafileWith(new object[] { TdRule(0) }), 2000).Should().Be(0);
    }

    [Fact]
    public void GrowingARollout_KeepsEveryoneAlreadyInIt()
    {
        FlagDatafile small = DatafileWith(new object[] { TdRule(0.05) });
        FlagDatafile large = DatafileWith(new object[] { TdRule(0.3) });
        for (int i = 0; i < 2000; i++)
        {
            EvalContext ctx = EvalContext.User(UserKey(i));
            if (Evaluator.EvaluateFlag(FlagKey, false, ctx, small).RuleId != RuleId) continue;
            Evaluator.EvaluateFlag(FlagKey, false, ctx, large).RuleId.Should().Be(RuleId);
        }
    }

    // -----------------------------------------------------------------------
    // Rollout membership.
    // -----------------------------------------------------------------------

    [Fact]
    public void TheAbTestHashInput_DecidesMembershipForBothRuleTypes()
    {
        FlagDatafile asTargeted = DatafileWith(new object[] { TdRule(0.3) });
        FlagDatafile asAbTest = DatafileWith(new object[] { TdRule(0.3, type: "ab_test") });
        for (int i = 0; i < 2000; i++)
        {
            EvalContext ctx = EvalContext.User(UserKey(i));
            bool inRollout = AllocationBucket(UserKey(i)) < 3000;
            (Evaluator.EvaluateFlag(FlagKey, false, ctx, asTargeted).RuleId == RuleId).Should().Be(inRollout);
            (Evaluator.EvaluateFlag(FlagKey, false, ctx, asAbTest).RuleId == RuleId).Should().Be(inRollout);
        }
    }

    [Fact]
    public void TheFirstUsers_BucketExactlyAsTheReferenceDoes()
    {
        Enumerable.Range(0, 5).Select(i => AllocationBucket(UserKey(i)))
            .Should().Equal(5278u, 8498u, 4278u, 4172u, 1088u);
    }

    [Fact]
    public void TheThresholdIsTheRawFloat_SoBucket1234IsInA012345Rollout()
    {
        FlagDatafile datafile = DatafileWith(new object[] { TdRule(0.12345) });
        string inside = KeyForAllocationBucket(1234, "td-in");
        string outside = KeyForAllocationBucket(1235, "td-out");
        // The same keys the TypeScript search settles on.
        (inside, outside).Should().Be(("td-in-3679", "td-out-19194"));

        Evaluator.EvaluateFlag(FlagKey, false, EvalContext.User(inside), datafile).RuleId.Should().Be(RuleId);

        var reasons = new List<string>();
        Evaluator.EvalResult result =
            Evaluator.EvaluateFlag(FlagKey, false, EvalContext.User(outside), datafile, reasons);
        result.Source.Should().Be(EvaluationSource.Default);
        reasons.Should().Equal(
            $"rule \"{RuleId}\" excluded by traffic allocation (hash 1235 >= threshold 1234.5)",
            "no rules matched, returning default variation");
    }

    [Fact]
    public void AVisitorOutsideTheRollout_FallsThroughToTheNextRule()
    {
        FlagDatafile datafile = DatafileWith(new object[]
        {
            TdRule(0.3),
            TdRule(null, id: "rule_everyone", variationId: "var_fallback"),
        });
        string outside = KeyForAllocationBucket(9000, "td-next");

        Evaluator.EvalResult result = Evaluator.EvaluateFlag(FlagKey, false, EvalContext.User(outside), datafile);
        result.RuleId.Should().Be("rule_everyone");
        result.VariationKey.Should().Be("fallback");
    }

    [Fact]
    public void AnAudienceMiss_StillSkipsBeforeTheRolloutIsConsulted()
    {
        var paid = new Dictionary<string, object?>
        {
            ["id"] = "aud_paid",
            ["rules"] = new Dictionary<string, object?>
            {
                ["id"] = "g_root",
                ["logic"] = "AND",
                ["conditions"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = "c_plan",
                        ["type"] = "CUSTOM_ATTRIBUTE",
                        ["attribute"] = "plan",
                        ["operator"] = "equals",
                        ["value"] = "paid",
                        ["contextKind"] = "user",
                    },
                },
            },
        };
        FlagDatafile datafile = DatafileWith(
            new object[] { TdRule(1, audienceIds: new[] { "aud_paid" }) }, new object[] { paid });
        var reasons = new List<string>();
        EvalContext freeUser = EvalContext.User("free-user",
            attributes: new Dictionary<string, object?> { ["plan"] = "free" });

        Evaluator.EvalResult result = Evaluator.EvaluateFlag(FlagKey, false, freeUser, datafile, reasons);

        result.Source.Should().Be(EvaluationSource.Default);
        reasons[0].Should().Be($"rule \"{RuleId}\" skipped (audience not matched)");
    }

    [Fact]
    public void ARuleWithNoVariations_StaysASilentSkipWhateverItsAllocation()
    {
        FlagDatafile datafile = DatafileWith(new object[] { TdRule(0, noVariations: true) });
        var reasons = new List<string>();
        Evaluator.EvaluateFlag(FlagKey, false, EvalContext.User("u1"), datafile, reasons);
        reasons.Should().Equal("no rules matched, returning default variation");
    }

    [Fact]
    public void ATargetedDeliveryDecision_FiresNoExposure_RolloutOrNot()
    {
        var exposures = new List<ExposureEvent>();
        string inside = KeyForAllocationBucket(10, "td-expo");

        Evaluator.EvalResult result = Evaluator.EvaluateFlag(FlagKey, false, EvalContext.User(inside),
            DatafileWith(new object[] { TdRule(0.3) }), options: new EvaluateOptions { OnExposure = exposures.Add });

        result.RuleType.Should().Be(RuleType.TargetedDelivery);
        exposures.Should().BeEmpty();
    }
}
