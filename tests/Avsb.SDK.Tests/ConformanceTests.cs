using System.Text.Json;
using System.Text.Json.Serialization;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// Conformance harness: loads every case from
/// packages/avsb-conformance/fixtures/manifest.json and asserts that
/// the .NET evaluator produces identical outcomes to the reference
/// TypeScript implementation.
/// </summary>
public sealed class ConformanceTests
{
    private static readonly string FixturesRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "avsb-conformance", "fixtures"));

    [Fact]
    public void AllFixturesRootExists()
    {
        Directory.Exists(FixturesRoot).Should().BeTrue(
            $"conformance fixtures directory should exist at {FixturesRoot}");
    }

    public static IEnumerable<object[]> ConformanceCases()
    {
        var manifestPath = Path.Combine(FixturesRoot, "manifest.json");
        if (!File.Exists(manifestPath)) return Enumerable.Empty<object[]>();

        var manifest = JsonSerializer.Deserialize<ConformanceManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        return manifest.Cases.Select(c => new object[] { c.Case, c.Expected });
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void ConformanceCase_MatchesExpected(string caseName, string expectedRelPath)
    {
        var expectedPath = Path.Combine(FixturesRoot, expectedRelPath);
        var spec = JsonSerializer.Deserialize<ConformanceSpec>(
            File.ReadAllText(expectedPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var datafile = InMemoryDatafileLoader.LoadFile(
            Path.Combine(FixturesRoot, spec.Datafile));

        var contextJson = File.ReadAllText(Path.Combine(FixturesRoot, spec.Context));
        var ctx = ParseContext(contextJson);

        var defaultValue = ParseDefault(spec.DefaultValue);
        var reasons = new List<string>();
        var result = Evaluator.EvaluateFlag(spec.FlagKey, defaultValue, ctx, datafile, reasons);

        // Source
        var expectedSource = ParseSource(spec.Expected.Source);
        result.Source.Should().Be(expectedSource,
            $"case '{caseName}': source mismatch");

        // RuleId
        result.RuleId.Should().Be(spec.Expected.RuleId,
            $"case '{caseName}': ruleId mismatch");

        // RuleType
        var expectedRuleType = spec.Expected.RuleType is null
            ? (RuleType?)null
            : ParseRuleType(spec.Expected.RuleType);
        result.RuleType.Should().Be(expectedRuleType,
            $"case '{caseName}': ruleType mismatch");

        // VariationKey
        result.VariationKey.Should().Be(spec.Expected.VariationKey,
            $"case '{caseName}': variationKey mismatch");

        // Value
        string actualValue = result.Value?.ToString() ?? "null";
        string expectedValue = spec.Expected.Value?.ToString() ?? "null";
        actualValue.Should().Be(expectedValue,
            $"case '{caseName}': value mismatch");

        // Reasons (each expected reason must appear in actual)
        foreach (var expectedReason in spec.Expected.Reasons)
        {
            reasons.Should().ContainSingle(r => r == expectedReason,
                $"case '{caseName}': reason '{expectedReason}' not found");
        }
    }

    // -----------------------------------------------------------------------
    // Parsing helpers.
    // -----------------------------------------------------------------------

    private static EvalContext ParseContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("kind", out var kindProp) &&
            kindProp.GetString() == "multi")
        {
            var contexts = new Dictionary<string, SingleContext>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "kind") continue;
                contexts[prop.Name] = ParseSingleContext(prop.Value, prop.Name);
            }
            return EvalContext.FromMulti(new MultiContext { Contexts = contexts });
        }

        return EvalContext.FromSingle(ParseSingleContext(root,
            root.TryGetProperty("kind", out var k) ? k.GetString() ?? "user" : "user"));
    }

    private static SingleContext ParseSingleContext(JsonElement el, string kind)
    {
        string key = el.TryGetProperty("key", out var kp) ? kp.GetString() ?? "" : "";
        string? name = el.TryGetProperty("name", out var np) ? np.GetString() : null;
        var attrs = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name is "kind" or "key" or "name" or "_meta") continue;
            attrs[prop.Name] = Evaluator.UnwrapJson(prop.Value);
        }
        return new SingleContext { Kind = kind, Key = key, Name = name, Attributes = attrs };
    }

    private static object? ParseDefault(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        _ => el.GetRawText(),
    };

    private static EvaluationSource ParseSource(string s) => s switch
    {
        "not_found" => EvaluationSource.NotFound,
        "disabled" => EvaluationSource.Disabled,
        "override" => EvaluationSource.DatafileOverride,
        "rule" => EvaluationSource.Rule,
        "holdout" => EvaluationSource.Holdout,
        "default" => EvaluationSource.Default,
        "sticky" => EvaluationSource.Sticky,
        "bandit" => EvaluationSource.Bandit,
        _ => EvaluationSource.NotFound,
    };

    private static RuleType ParseRuleType(string s) => s switch
    {
        "targeted_delivery" => RuleType.TargetedDelivery,
        "ab_test" => RuleType.AbTest,
        "holdout" => RuleType.Holdout,
        "bandit" => RuleType.Bandit,
        _ => RuleType.TargetedDelivery,
    };

    // -----------------------------------------------------------------------
    // Manifest + spec shapes.
    // -----------------------------------------------------------------------

    private sealed class ConformanceManifest
    {
        [JsonPropertyName("cases")] public List<ManifestCase> Cases { get; set; } = new();
    }

    private sealed class ManifestCase
    {
        [JsonPropertyName("case")] public string Case { get; set; } = string.Empty;
        [JsonPropertyName("expected")] public string Expected { get; set; } = string.Empty;
    }

    private sealed class ConformanceSpec
    {
        [JsonPropertyName("datafile")] public string Datafile { get; set; } = string.Empty;
        [JsonPropertyName("context")] public string Context { get; set; } = string.Empty;
        [JsonPropertyName("flagKey")] public string FlagKey { get; set; } = string.Empty;
        [JsonPropertyName("defaultValue")] public JsonElement DefaultValue { get; set; }
        [JsonPropertyName("expected")] public ConformanceExpected Expected { get; set; } = new();
    }

    private sealed class ConformanceExpected
    {
        [JsonPropertyName("value")] public JsonElement Value { get; set; }
        [JsonPropertyName("variationKey")] public string? VariationKey { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("ruleId")] public string? RuleId { get; set; }
        [JsonPropertyName("ruleType")] public string? RuleType { get; set; }
        [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = new();
    }
}
