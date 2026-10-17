using System.Text.Json;
using Avsb.SDK.Core;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

/// <summary>
/// The version grammar. The rows are the ones packages/avsb-core/src/semver.test.ts runs, so
/// every port is held to the same grammar, not just the same corpus.
/// </summary>
public sealed class SemVerTests
{
    private static readonly string[] Operators =
        { "semver_eq", "semver_neq", "semver_gt", "semver_gte", "semver_lt", "semver_lte" };

    private static SemVer.Parsed MustParse(string input)
    {
        SemVer.Parsed? version = SemVer.Parse(input);
        version.Should().NotBeNull($"test row {input} must parse");
        return version!;
    }

    public static IEnumerable<object[]> ReadableVersions() => new[]
    {
        new object[] { "1.2.3", "1", "2", "3", "" },
        new object[] { "0.0.0", "0", "0", "0", "" },
        new object[] { "v1.2.3", "1", "2", "3", "" },
        new object[] { "V1.2.3", "1", "2", "3", "" },
        new object[] { "2", "2", "0", "0", "" },
        new object[] { "2.3", "2", "3", "0", "" },
        new object[] { "v2", "2", "0", "0", "" },
        new object[] { "  2.3.0\t", "2", "3", "0", "" },
        new object[] { "\n\v\f1.0.0\r\n", "1", "0", "0", "" },
        new object[] { "1.0.0-alpha", "1", "0", "0", "alpha" },
        new object[] { "1.0.0-alpha.1", "1", "0", "0", "alpha|1" },
        new object[] { "1.0.0-0.3.7", "1", "0", "0", "0|3|7" },
        new object[] { "1.0.0-x-y-z.--", "1", "0", "0", "x-y-z|--" },
        new object[] { "1.0.0-0a", "1", "0", "0", "0a" },
        new object[] { "1.0.0+20130313144700", "1", "0", "0", "" },
        new object[] { "1.0.0-beta+exp.sha.5114f85", "1", "0", "0", "beta" },
        new object[] { "1.0.0+21AF26D3----117B344092BD", "1", "0", "0", "" },
        new object[] { "1.0.0+001", "1", "0", "0", "" },
        new object[] { "2-beta", "2", "0", "0", "beta" },
        new object[] { "1-2.3", "1", "0", "0", "2|3" },
        new object[] { "18446744073709551616.0.0", "18446744073709551616", "0", "0", "" },
    };

    [Theory]
    [MemberData(nameof(ReadableVersions))]
    public void Parse_ReadsTheGrammar(string input, string major, string minor, string patch, string prerelease)
    {
        SemVer.Parsed version = MustParse(input);
        version.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
        string.Join("|", version.Prerelease).Should().Be(prerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("V")]
    [InlineData("banana")]
    [InlineData("2.x")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3+build..1")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1..2")]
    [InlineData("vv1.2.3")]
    [InlineData("v 1.2.3")]
    [InlineData("1.2.3 beta")]
    [InlineData("1.2.3-beta_1")]
    [InlineData("1.2.3-béta")]
    [InlineData("١.٢.٣")]
    [InlineData("1.2.3+build+more")]
    [InlineData("=1.2.3")]
    [InlineData("-1.2.3")]
    [InlineData("+1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3\n2.0.0")]
    [InlineData(" 1 .2.3")]
    public void Parse_RefusesEverythingElse(string input)
    {
        SemVer.Parse(input).Should().BeNull();
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("2.0.0", "2.1.0", -1)]
    [InlineData("2.1.0", "2.1.1", -1)]
    [InlineData("2.10.0", "2.9.1", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2", "2.0.0", 0)]
    [InlineData("v2.3", "2.3.0", 0)]
    [InlineData("1.0.0+build1", "1.0.0+build2", 0)]
    [InlineData("1.0.0-rc.1+build.5", "1.0.0-rc.1", 0)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    [InlineData("1.0.0-Beta", "1.0.0-alpha", -1)]
    [InlineData("1.0.0-1", "1.0.0-a", -1)]
    [InlineData("1.0.0-2", "1.0.0-10", -1)]
    [InlineData("1.0.0-a-b", "1.0.0-a", 1)]
    [InlineData("18446744073709551616.0.0", "18446744073709551615.0.0", 1)]
    [InlineData("99999999999999999999.0.0", "100000000000000000000.0.0", -1)]
    public void Compare_FollowsSemVerPrecedence(string a, string b, int order)
    {
        SemVer.Compare(MustParse(a), MustParse(b)).Should().Be(order);
        SemVer.Compare(MustParse(b), MustParse(a)).Should().Be(-order);
    }

    [Fact]
    public void Compare_OrdersThePrereleaseChain()
    {
        string[] chain =
        {
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
        };
        for (int i = 0; i < chain.Length - 1; i++)
        {
            SemVer.Compare(MustParse(chain[i]), MustParse(chain[i + 1])).Should().Be(-1, $"{chain[i]} < {chain[i + 1]}");
            SemVer.Compare(MustParse(chain[i + 1]), MustParse(chain[i])).Should().Be(1, $"{chain[i + 1]} > {chain[i]}");
        }
    }

    [Theory]
    [InlineData("semver_eq", "2.3.0", "2.3", true)]
    [InlineData("semver_eq", "2.3.1", "2.3", false)]
    [InlineData("semver_neq", "2.3.1", "2.3.0", true)]
    [InlineData("semver_neq", "v2.3.0", "2.3", false)]
    [InlineData("semver_gt", "2.10.0", "2.9.1", true)]
    [InlineData("semver_gt", "2.9.1", "2.9.1", false)]
    [InlineData("semver_gte", "2.9.1", "2.9.1", true)]
    [InlineData("semver_gte", "2.9.0", "2.9.1", false)]
    [InlineData("semver_lt", "2.0.0-beta.2", "2.0.0", true)]
    [InlineData("semver_lt", "2.0.0", "2.0.0", false)]
    [InlineData("semver_lte", "2.0.0", "2.0.0", true)]
    [InlineData("semver_lte", "2.0.1", "2.0.0", false)]
    public void Evaluate_AnswersEachOperator(string op, string actual, string expected, bool result)
    {
        SemVer.Evaluate(op, actual, expected).Should().Be(result);
    }

    [Fact]
    public void Evaluate_ReadsANumberTheWayJavaScriptPrintsIt()
    {
        SemVer.Evaluate("semver_eq", 2, "2.0.0").Should().BeTrue();
        SemVer.Evaluate("semver_eq", 2.0d, "2.0.0").Should().BeTrue();
        SemVer.Evaluate("semver_eq", 2L, "2.0.0").Should().BeTrue();
        SemVer.Evaluate("semver_eq", 2.0m, "2.0.0").Should().BeTrue();
        SemVer.Evaluate("semver_lt", 2.5d, "2.10.0").Should().BeTrue();
        SemVer.Evaluate("semver_eq", -0.0d, "0.0.0").Should().BeTrue();
        SemVer.Evaluate("semver_eq", JsonDocument.Parse("2.0").RootElement, "2.0.0").Should().BeTrue();
        SemVer.Evaluate("semver_gte", -2, "0.0.0").Should().BeFalse();
        SemVer.Evaluate("semver_gte", 1e21, "0.0.0").Should().BeFalse();
    }

    [Fact]
    public void Evaluate_FailsClosedOnWrongTypesAndUnparsableSides()
    {
        object?[] wrongTypes =
        {
            true, false, null, new Dictionary<string, object?>(), new List<object?> { "2.0.0" },
            JsonDocument.Parse("true").RootElement, JsonDocument.Parse("[\"2.0.0\"]").RootElement,
        };
        foreach (string op in Operators)
        {
            foreach (object? actual in wrongTypes)
            {
                SemVer.Evaluate(op, actual, "2.0.0").Should().BeFalse($"{op} on {actual ?? "null"}");
            }
            SemVer.Evaluate(op, "3.0.0", "2.x").Should().BeFalse($"{op} with a bad rule value");
            SemVer.Evaluate(op, "1.2.3.4", "2.0.0").Should().BeFalse($"{op} with a bad attribute");
        }
        SemVer.Evaluate("semver_eq", "2.0.0", 2.0d).Should().BeFalse("the rule value must be a string");
        SemVer.Evaluate("semver_between", "2.0.0", "2.0.0").Should().BeFalse("an unknown operator");
        SemVer.Evaluate(null, "2.0.0", "2.0.0").Should().BeFalse("a null operator");
    }

    private static Condition VersionCondition(string op, object value) =>
        JsonSerializer.Deserialize<Condition>(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = "c1",
                ["type"] = "CUSTOM_ATTRIBUTE",
                ["attribute"] = "appVersion",
                ["operator"] = op,
                ["value"] = value,
            }),
            JsonOptions.Default)!;

    [Fact]
    public void RoutesThroughTheAudienceEvaluator()
    {
        var newer = EvalContext.User("u1", attributes: new Dictionary<string, object?> { ["appVersion"] = "2.10.0" });
        var older = EvalContext.User("u2", attributes: new Dictionary<string, object?> { ["appVersion"] = "2.9.0" });

        AudienceEvaluator.EvaluateCondition(VersionCondition("semver_gt", "2.9.1"), newer).Should().BeTrue();
        AudienceEvaluator.EvaluateCondition(VersionCondition("semver_gt", "2.9.1"), older).Should().BeFalse();
        AudienceEvaluator.EvaluateCondition(VersionCondition("semver_gte", new[] { "2.3.0", "9.9.9" }), newer)
            .Should().BeTrue("a list value compares against its first entry");
        AudienceEvaluator.EvaluateCondition(VersionCondition("semver_neq", "2.0.0"), EvalContext.User("u3"))
            .Should().BeFalse("semver_neq on a missing attribute is false");
    }
}
