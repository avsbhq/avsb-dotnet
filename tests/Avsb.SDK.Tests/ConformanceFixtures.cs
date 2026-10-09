using System.Text.Json;
using Avsb.SDK.Core;

namespace Avsb.SDK.Tests;

/// <summary>
/// Shared access to the conformance corpus for every test class that reads it.
///
/// The fixtures root used to be resolved in several classes by counting ".." segments from the
/// build output directory. That count is off by one for the standard bin/{Configuration}/{TFM}
/// layout, so those classes all threw DirectoryNotFoundException the first time this test project
/// compiled. One resolver now walks upward until it finds the corpus, and falls back to the copy
/// vendored into this test project, which is what the published repo carries.
///
/// FAIL-HARD RULE (conformance README): no copy at all throws, naming every path tried.
/// </summary>
internal static class ConformanceFixtures
{
    private const int MaxUpwardLevels = 8;

    private static readonly Lazy<string> LazyRoot = new(ResolveRoot);

    /// <summary>The fixtures directory (the one holding manifest.json).</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>Full path of datafiles/{name}.datafile.json.</summary>
    public static string DatafilePath(string name) =>
        Path.Combine(Root, "datafiles", $"{name}.datafile.json");

    /// <summary>
    /// The monorepo copy first, at any level, then the vendored copy, as the conformance README
    /// orders them. Checking both at each level in turn found the vendored copy (three levels up)
    /// before the authoritative one (five levels up), so a stale vendored copy would have hidden a
    /// monorepo change.
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        foreach (DirectoryInfo dir in Ancestors())
        {
            // Monorepo layout, from anywhere under packages/avsb-dotnet.
            yield return Path.Combine(dir.FullName, "avsb-conformance", "fixtures");
            yield return Path.Combine(dir.FullName, "packages", "avsb-conformance", "fixtures");
        }
        foreach (DirectoryInfo dir in Ancestors())
        {
            // Published repo: vendored copy that travels with the test project.
            yield return Path.Combine(dir.FullName, "vendor", "avsb-conformance", "fixtures");
        }
    }

    private static IEnumerable<DirectoryInfo> Ancestors()
    {
        DirectoryInfo? cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < MaxUpwardLevels && cursor is not null; i++)
        {
            yield return cursor;
            cursor = cursor.Parent;
        }
    }

    private static string ResolveRoot()
    {
        var looked = new List<string>();
        foreach (var candidate in Candidates())
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

    // -----------------------------------------------------------------------
    // Parsing helpers, shared by the single-context and allocation families.
    // -----------------------------------------------------------------------

    public static EvalContext ParseContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseContext(doc.RootElement);
    }

    public static EvalContext ParseContext(JsonElement root)
    {
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

    public static object? ParseDefault(JsonElement el) => el.ValueKind switch
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
    /// The mapping lives on the enum itself, so the SDK and this harness cannot drift.
    /// </summary>
    public static EvaluationSource ParseSource(string s) => EvaluationSourceExtensions.FromWire(s);

    public static RuleType ParseRuleType(string s) => s switch
    {
        "targeted_delivery" => RuleType.TargetedDelivery,
        "ab_test" => RuleType.AbTest,
        "holdout" => RuleType.Holdout,
        "bandit" => RuleType.Bandit,
        _ => throw new InvalidOperationException(
            $"Fixture declares ruleType '{s}', which this SDK does not map."),
    };

    // -----------------------------------------------------------------------
    // Structural JSON comparison.
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when the SDK's value is the same JSON value the fixture recorded. The SDK side is a
    /// JsonElement when a variation was served and a plain CLR object (the caller's default) when
    /// it was not, so both are normalised before comparing.
    /// </summary>
    public static bool JsonValuesEqual(JsonElement expected, object? actual)
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

    public static string Describe(JsonElement element) =>
        $"{element.ValueKind} {element.GetRawText()}";

    public static string DescribeActual(object? actual) => actual switch
    {
        null => "null (CLR null)",
        JsonElement element => Describe(element),
        _ => $"{actual.GetType().Name} {JsonSerializer.Serialize(actual)}",
    };
}
