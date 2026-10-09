using System.Text.Json;
using System.Text.Json.Serialization;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// Conformance harness: loads every case from the conformance corpus manifest
/// and asserts that the .NET evaluator produces identical outcomes to the
/// reference TypeScript implementation.
///
/// FAIL-HARD RULE (conformance README): a missing manifest, an empty manifest,
/// or a missing per-case file throws. The fixtures root is resolved by
/// <see cref="ConformanceFixtures"/>, which walks upward until it finds the
/// corpus and falls back to the copy vendored into this test project, which is
/// what the published repo carries.
/// </summary>
public sealed class ConformanceTests
{
    private static string FixturesRoot => ConformanceFixtures.Root;

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static T ReadFixture<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Conformance fixture missing: {path} ({what}). " +
                "Run `npm run generate` in packages/avsb-conformance.");
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidOperationException(
                   $"Conformance fixture parsed to null: {path} ({what}).");
    }

    private static ConformanceManifest LoadManifest()
    {
        var manifest = ReadFixture<ConformanceManifest>(
            Path.Combine(FixturesRoot, "manifest.json"), "manifest");

        if (manifest.Cases.Count == 0)
        {
            throw new InvalidOperationException(
                $"Conformance manifest at {FixturesRoot} lists no cases. " +
                "An empty corpus guarantees nothing.");
        }
        if (manifest.CaseCount != 0 && manifest.CaseCount != manifest.Cases.Count)
        {
            throw new InvalidOperationException(
                $"Conformance manifest says caseCount={manifest.CaseCount} but carries " +
                $"{manifest.Cases.Count} cases, so it was hand-edited.");
        }
        return manifest;
    }

    [Fact]
    public void CorpusIsPresentAndNotEmpty()
    {
        LoadManifest().Cases.Should().NotBeEmpty(
            $"the conformance corpus at {FixturesRoot} must carry cases");
    }

    /// <summary>
    /// Fixture files the manifest does not name are renamed or deleted cases
    /// whose output lingers: they replay green against the engine that produced
    /// them while locking semantics nobody declares.
    /// </summary>
    [Fact]
    public void NoOrphanFixtures()
    {
        var named = LoadManifest().Cases.Select(c => $"{c.Case}.json").ToHashSet();
        var orphans = Directory
            .EnumerateFiles(Path.Combine(FixturesRoot, "expected"), "*.json")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !named.Contains(name))
            .ToList();

        orphans.Should().BeEmpty("every fixture file must be named by the manifest");
    }

    public static IEnumerable<object[]> ConformanceCases()
    {
        // Throwing here is deliberate: xUnit surfaces it as a failure, whereas
        // returning an empty sequence used to read as "nothing to test".
        return LoadManifest().Cases.Select(c => new object[] { c.Case, c.Expected });
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void ConformanceCase_MatchesExpected(string caseName, string expectedRelPath)
    {
        var spec = ReadFixture<ConformanceSpec>(
            Path.Combine(FixturesRoot, expectedRelPath), $"manifest case {caseName}");

        spec.Case.Should().Be(caseName,
            "the fixture must declare the same case id as the manifest");

        var datafilePath = Path.Combine(FixturesRoot, spec.Datafile);
        if (!File.Exists(datafilePath))
        {
            throw new InvalidOperationException(
                $"Conformance datafile missing: {datafilePath} (case {caseName}).");
        }
        var datafile = InMemoryDatafileLoader.LoadFile(datafilePath);

        var contextPath = Path.Combine(FixturesRoot, spec.Context);
        if (!File.Exists(contextPath))
        {
            throw new InvalidOperationException(
                $"Conformance context missing: {contextPath} (case {caseName}).");
        }
        var ctx = ConformanceFixtures.ParseContext(File.ReadAllText(contextPath));

        var defaultValue = ConformanceFixtures.ParseDefault(spec.DefaultValue);
        // Reasons are part of the corpus, so the harness always collects them.
        var reasons = new List<string>();
        var result = Evaluator.EvaluateFlag(spec.FlagKey, defaultValue, ctx, datafile, reasons);

        // Source
        result.Source.Should().Be(ConformanceFixtures.ParseSource(spec.Expected.Source),
            $"case '{caseName}': source mismatch");

        // RuleId
        result.RuleId.Should().Be(spec.Expected.RuleId,
            $"case '{caseName}': ruleId mismatch");

        // RuleType
        var expectedRuleType = spec.Expected.RuleType is null
            ? (RuleType?)null
            : ConformanceFixtures.ParseRuleType(spec.Expected.RuleType);
        result.RuleType.Should().Be(expectedRuleType,
            $"case '{caseName}': ruleType mismatch");

        // VariationKey
        result.VariationKey.Should().Be(spec.Expected.VariationKey,
            $"case '{caseName}': variationKey mismatch");

        // Value. Compared STRUCTURALLY, by JSON kind and value.
        //
        // It used to compare ToString() against ToString(), which passes when a number flag hands
        // back the string "1" instead of the number 1, when a JSON object's keys are ordered
        // differently or its source text is indented differently, and when the boolean true
        // arrives as the text "true": exactly the type-fidelity failures the `typed` fixture
        // family exists to catch. (Keeping it COMPILING matters too: `Value?.ToString()` on a
        // JsonElement, a non-nullable struct, did not compile at all before session 2 fixed it, so
        // this must not regress into that shape.)
        ConformanceFixtures.JsonValuesEqual(spec.Expected.Value, result.Value).Should().BeTrue(
            $"case '{caseName}': value mismatch. Expected {ConformanceFixtures.Describe(spec.Expected.Value)} but got "
            + ConformanceFixtures.DescribeActual(result.Value));

        // Reasons — exact, ordered comparison (spec section 1.4).
        reasons.Should().Equal(spec.Expected.Reasons,
            $"case '{caseName}': reasons mismatch");
    }

    // -----------------------------------------------------------------------
    // Manifest + spec shapes.
    // -----------------------------------------------------------------------

    private sealed class ConformanceManifest
    {
        [JsonPropertyName("caseCount")] public int CaseCount { get; set; }
        [JsonPropertyName("cases")] public List<ManifestCase> Cases { get; set; } = new();
    }

    private sealed class ManifestCase
    {
        [JsonPropertyName("case")] public string Case { get; set; } = string.Empty;
        [JsonPropertyName("family")] public string Family { get; set; } = string.Empty;
        [JsonPropertyName("expected")] public string Expected { get; set; } = string.Empty;
    }

    private sealed class ConformanceSpec
    {
        [JsonPropertyName("case")] public string Case { get; set; } = string.Empty;
        [JsonPropertyName("family")] public string Family { get; set; } = string.Empty;
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
