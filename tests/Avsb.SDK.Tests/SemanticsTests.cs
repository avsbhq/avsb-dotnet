using Avsb.SDK.Core;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// The semantics the conformance corpus declares but cannot generate: what IsEnabled and Exists
/// answer, which source spellings are legal, and what not_ready means.
///
/// Every case is a row of packages/avsb-conformance/fixtures/semantics/manifest.json. They live in
/// a unit test rather than the generated corpus because none of them can be expressed as a
/// datafile plus a context.
/// </summary>
public sealed class SemanticsTests
{
    private static Flag<object?> Make(EvaluationSource source, object? value) => new()
    {
        Value = value,
        Source = source,
        Reasons = Array.Empty<string>(),
    };

    [Fact]
    public void IsEnabled_TrueOnlyForADecidedSourceWithATruthyValue()
    {
        Make(EvaluationSource.Rule, true).IsEnabled().Should().BeTrue();
        Make(EvaluationSource.Holdout, true).IsEnabled().Should().BeTrue();
        Make(EvaluationSource.Bandit, "bold-copy").IsEnabled().Should().BeTrue();
        Make(EvaluationSource.RuntimeOverride, true).IsEnabled().Should().BeTrue();
        Make(EvaluationSource.DatafileOverride, "forced-on").IsEnabled().Should().BeTrue();
        Make(EvaluationSource.Sticky, true).IsEnabled().Should().BeTrue();

        Make(EvaluationSource.Rule, false).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.Bandit, "").IsEnabled().Should().BeFalse();
        Make(EvaluationSource.RuntimeOverride, 0).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.DatafileOverride, null).IsEnabled().Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_FalseForEveryFallbackSourceHoweverTruthyTheValue()
    {
        // The trap that made two ports disagree: a flag serving its DEFAULT variation is not
        // enabled even when that default is true.
        Make(EvaluationSource.Default, true).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.NotFound, true).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.Disabled, true).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.NotReady, true).IsEnabled().Should().BeFalse(
            "an uninitialised SDK never reports a feature as enabled");
    }

    [Fact]
    public void IsEnabled_FollowsJavaScriptTruthinessNotDotNets()
    {
        Make(EvaluationSource.Rule, "false").IsEnabled().Should().BeTrue("the STRING false is truthy");
        Make(EvaluationSource.Rule, "0").IsEnabled().Should().BeTrue("the STRING zero is truthy");
        Make(EvaluationSource.Rule, -1).IsEnabled().Should().BeTrue("negative numbers are truthy");
        Make(EvaluationSource.Rule, 0).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.Rule, "").IsEnabled().Should().BeFalse();
        Make(EvaluationSource.Rule, double.NaN).IsEnabled().Should().BeFalse();
        Make(EvaluationSource.Rule, new List<object?>()).IsEnabled().Should().BeTrue(
            "an empty array is truthy; Python would disagree");
    }

    [Fact]
    public void Exists_FalseOnlyWhenTheKeyIsUnknownOrUnknowable()
    {
        Make(EvaluationSource.Rule, true).Exists().Should().BeTrue();
        Make(EvaluationSource.Default, true).Exists().Should().BeTrue();
        Make(EvaluationSource.Disabled, true).Exists().Should().BeTrue(
            "a disabled flag exists; the customer turned it off");
        Make(EvaluationSource.NotFound, true).Exists().Should().BeFalse();
        Make(EvaluationSource.NotReady, true).Exists().Should().BeFalse(
            "with no datafile, whether the key exists is unknowable");
    }

    [Fact]
    public void EverySourceHasAWireSpellingAndRoundTrips()
    {
        var expected = new Dictionary<EvaluationSource, string>
        {
            [EvaluationSource.DatafileOverride] = "datafileOverride",
            [EvaluationSource.RuntimeOverride] = "runtimeOverride",
            [EvaluationSource.Sticky] = "sticky",
            [EvaluationSource.Rule] = "rule",
            [EvaluationSource.Holdout] = "holdout",
            [EvaluationSource.Bandit] = "bandit",
            [EvaluationSource.Default] = "default",
            [EvaluationSource.NotFound] = "not_found",
            [EvaluationSource.Disabled] = "disabled",
            [EvaluationSource.NotReady] = "not_ready",
        };

        Enum.GetValues<EvaluationSource>().Should().HaveCount(expected.Count,
            "a new source needs a wire spelling and a row in this table");
        foreach (KeyValuePair<EvaluationSource, string> pair in expected)
        {
            pair.Key.WireValue().Should().Be(pair.Value);
            EvaluationSourceExtensions.FromWire(pair.Value).Should().Be(pair.Key);
        }
    }

    [Theory]
    [InlineData("not_started")]
    [InlineData("not_initialized")]
    [InlineData("uninitialized")]
    [InlineData("error")]
    public void AnInventedNotReadySpellingIsRejectedRatherThanSilentlyMapped(string banned)
    {
        Action parse = () => EvaluationSourceExtensions.FromWire(banned);
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LegacySpellingsMapAtTheBoundary()
    {
        EvaluationSourceExtensions.FromWire("override").Should().Be(EvaluationSource.DatafileOverride);
        EvaluationSourceExtensions.FromWire("runtime_override").Should().Be(EvaluationSource.RuntimeOverride);
    }

    [Fact]
    public void NotReadyCarriesTheCallerDefaultWithNothingInvented()
    {
        Flag<string> notReady = Flag<string>.NotReady("my-default", "SDK is not ready");
        notReady.Value.Should().Be("my-default", "the caller's value, type intact");
        notReady.Source.Should().Be(EvaluationSource.NotReady);
        notReady.VariationKey.Should().BeNull();
        notReady.RuleId.Should().BeNull();
        notReady.RuleType.Should().BeNull();
        notReady.IsEnabled().Should().BeFalse();
        notReady.Exists().Should().BeFalse();
        notReady.Reasons.Should().HaveCount(1);
    }

    // -----------------------------------------------------------------------
    // JavaScript coercion, which every reason string and every bucket depends on.
    // -----------------------------------------------------------------------

    [Fact]
    public void Stringify_MatchesJavaScriptNumberToString()
    {
        // "1000.0" or a culture-formatted "1000,5" would fail every allocation reason.
        JsNumber.Stringify(1000.0).Should().Be("1000");
        JsNumber.Stringify(3330.0).Should().Be("3330");
        JsNumber.Stringify(0.0).Should().Be("0");
        JsNumber.Stringify(-0.0).Should().Be("0");
        JsNumber.Stringify(0.12345 * 10_000).Should().Be("1234.5");
        JsNumber.Stringify(0.25).Should().Be("0.25");
        JsNumber.Stringify(-2.5).Should().Be("-2.5");
        JsNumber.Stringify(1e21).Should().Be("1e+21");
        JsNumber.Stringify(1e20).Should().Be("100000000000000000000");
        JsNumber.Stringify(1e-6).Should().Be("0.000001");
        JsNumber.Stringify(1e-7).Should().Be("1e-7");
        JsNumber.Stringify(double.NaN).Should().Be("NaN");
        JsNumber.Stringify(double.PositiveInfinity).Should().Be("Infinity");
    }

    [Fact]
    public void StringOf_MatchesJavaScriptStringCoercion()
    {
        JsNumber.StringOf(null).Should().Be("undefined");
        JsNumber.StringOf(true).Should().Be("true");
        JsNumber.StringOf(42).Should().Be("42");
        JsNumber.StringOf(42.0).Should().Be("42");
        JsNumber.StringOf(42.5).Should().Be("42.5");
        JsNumber.StringOf("hello").Should().Be("hello");
        JsNumber.StringOf(new List<object?> { 1, null, "two" }).Should().Be("1,,two");
        JsNumber.StringOf(new Dictionary<string, object?>()).Should().Be("[object Object]");
    }

    [Fact]
    public void NumberOf_MatchesJavaScriptNumberCoercionIncludingItsOddCases()
    {
        JsNumber.NumberOf("").Should().Be(0.0, "Number(\"\") is 0 in JavaScript");
        JsNumber.NumberOf("5").Should().Be(5.0);
        JsNumber.NumberOf(" 5.5 ").Should().Be(5.5);
        JsNumber.NumberOf("1e3").Should().Be(1000.0);
        JsNumber.NumberOf(true).Should().Be(1.0);
        double.IsNaN(JsNumber.NumberOf("abc")).Should().BeTrue();
        double.IsNaN(JsNumber.NumberOf(null)).Should().BeTrue();
    }
}
