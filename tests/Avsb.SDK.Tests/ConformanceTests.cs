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
/// or a missing per-case file throws. The fixtures root also used to be
/// resolved by counting ".." segments from the build output directory, which is
/// off by one for the standard bin/{Configuration}/{TFM} layout and silently
/// produced an empty case list. It now walks upward until it finds the corpus,
/// and falls back to the copy vendored into this test project, which is what
/// the published repo carries.
/// </summary>
public sealed class ConformanceTests
{
    private const int MaxUpwardLevels = 8;

    private static readonly Lazy<string> LazyFixturesRoot = new(ResolveFixturesRoot);

    private static string FixturesRoot => LazyFixturesRoot.Value;

    private static IEnumerable<string> FixtureCandidates()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < MaxUpwardLevels && cursor is not null; i++)
        {
            // Monorepo layout, from anywhere under packages/avsb-dotnet.
            yield return Path.Combine(cursor.FullName, "avsb-conformance", "fixtures");
            yield return Path.Combine(cursor.FullName, "packages", "avsb-conformance", "fixtures");
            // Published repo: vendored copy that travels with the test project.
            yield return Path.Combine(cursor.FullName, "vendor", "avsb-conformance", "fixtures");
            cursor = cursor.Parent;
        }
    }

    private static string ResolveFixturesRoot()
    {
        var looked = new List<string>();
        foreach (var candidate in FixtureCandidates())
        {
            looked.Add(candidate);
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException(
            "Conformance fixtures not found. Looked for manifest.json in:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", looked) +
            Environment.NewLine +
            "In the monorepo, run `npm run generate` in packages/avsb-conformance. " +
            "For this repo standalone, run `npm run vendor` there and commit " +
            "tests/Avsb.SDK.Tests/vendor.");
    }

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
        var ctx = ParseContext(File.ReadAllText(contextPath));

        var defaultValue = ParseDefault(spec.DefaultValue);
        // Reasons are part of the corpus, so the harness always collects them.
        var reasons = new List<string>();
        var result = Evaluator.EvaluateFlag(spec.FlagKey, defaultValue, ctx, datafile, reasons);

        // Source
        result.Source.Should().Be(ParseSource(spec.Expected.Source),
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

        // Value. Compared STRUCTURALLY, by JSON kind and value.
        //
        // It used to compare ToString() against ToString(), which passes when a number flag hands
        // back the string "1" instead of the number 1, when a JSON object's keys are ordered
        // differently or its source text is indented differently, and when the boolean true
        // arrives as the text "true": exactly the type-fidelity failures the `typed` fixture
        // family exists to catch. (Keeping it COMPILING matters too: `Value?.ToString()` on a
        // JsonElement, a non-nullable struct, did not compile at all before session 2 fixed it, so
        // this must not regress into that shape.)
        JsonValuesEqual(spec.Expected.Value, result.Value).Should().BeTrue(
            $"case '{caseName}': value mismatch. Expected {Describe(spec.Expected.Value)} but got "
            + DescribeActual(result.Value));

        // Reasons — exact, ordered comparison (spec section 1.4).
        reasons.Should().Equal(spec.Expected.Reasons,
            $"case '{caseName}': reasons mismatch");
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

    /// <summary>
    /// Map a wire source string onto the enum. Unknown values throw: the previous fallback to
    /// NotFound turned "this SDK has never heard of that source" into a silently wrong assertion.
    /// The mapping now lives on the enum itself, so the SDK and this harness cannot drift, and
    /// not_ready is covered.
    /// </summary>
    private static EvaluationSource ParseSource(string s) => EvaluationSourceExtensions.FromWire(s);

    // -----------------------------------------------------------------------
    // Structural JSON comparison.
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when the SDK's value is the same JSON value the fixture recorded. The SDK side is a
    /// JsonElement when a variation was served and a plain CLR object (the caller's default) when
    /// it was not, so both are normalised before comparing.
    /// </summary>
    private static bool JsonValuesEqual(JsonElement expected, object? actual)
    {
        if (actual is JsonElement element) return JsonElementsEqual(expected, element);

        if (actual is null)
        {
            return expected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        }

        using JsonDocument serialised = JsonDocument.Parse(JsonSerializer.Serialize(actual));
        return JsonElementsEqual(expected, serialised.RootElement);
    }

    private static bool JsonElementsEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind is JsonValueKind.Undefined) return actual.ValueKind is JsonValueKind.Undefined;
        if (expected.ValueKind != actual.ValueKind) return false;

        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.String:
                return expected.GetString() == actual.GetString();
            case JsonValueKind.Number:
                // Numbers compare by value, not by text: 3 and 3.0 are the same JSON number, and
                // the reference engine cannot tell them apart either.
                return expected.GetDouble().Equals(actual.GetDouble());
            case JsonValueKind.Array:
            {
                JsonElement.ArrayEnumerator left = expected.EnumerateArray();
                JsonElement.ArrayEnumerator right = actual.EnumerateArray();
                while (true)
                {
                    bool leftNext = left.MoveNext();
                    bool rightNext = right.MoveNext();
                    if (leftNext != rightNext) return false;
                    if (!leftNext) return true;
                    if (!JsonElementsEqual(left.Current, right.Current)) return false;
                }
            }
            case JsonValueKind.Object:
            {
                int expectedCount = 0;
                foreach (JsonProperty property in expected.EnumerateObject())
                {
                    expectedCount++;
                    if (!actual.TryGetProperty(property.Name, out JsonElement other)) return false;
                    if (!JsonElementsEqual(property.Value, other)) return false;
                }
                int actualCount = 0;
                foreach (JsonProperty _ in actual.EnumerateObject()) actualCount++;
                return expectedCount == actualCount;
            }
            default:
                return false;
        }
    }

    private static string Describe(JsonElement element) =>
        $"{element.ValueKind} {element.GetRawText()}";

    private static string DescribeActual(object? actual) => actual switch
    {
        null => "null (CLR null)",
        JsonElement element => Describe(element),
        _ => $"{actual.GetType().Name} {JsonSerializer.Serialize(actual)}",
    };

    private static RuleType ParseRuleType(string s) => s switch
    {
        "targeted_delivery" => RuleType.TargetedDelivery,
        "ab_test" => RuleType.AbTest,
        "holdout" => RuleType.Holdout,
        "bandit" => RuleType.Bandit,
        _ => throw new InvalidOperationException(
            $"Fixture declares ruleType '{s}', which this SDK does not map."),
    };

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
