using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// The conformance allocation family: targeted delivery rollouts (N1-1).
///
/// Six generated fixtures, one rollout rule each (5, 30 and 50 percent, a fractional 0.12345
/// boundary, 100 percent and no allocation), each with 200 contexts whose outcomes the TypeScript
/// reference recorded. Every one of the 1,200 evaluations is replayed, reasons included, so a
/// visitor is inside or outside a rollout identically here and in every other SDK.
///
/// FAIL-HARD RULE (conformance README, "The allocation family"): a missing manifest, an empty or
/// miscounted fixture list, a missing file, a case that differs from its manifest entry, a fixture
/// with the wrong number of evaluations, or a JSON file the manifest does not name all throw.
/// </summary>
public sealed class ConformanceAllocationTests
{
    private static string AllocationDir => Path.Combine(ConformanceFixtures.Root, "allocation");

    private const string RegenerateHint =
        "Run `npm run generate` then `npm run vendor` in packages/avsb-conformance.";

    private static JsonElement ReadJson(string fileName)
    {
        string path = Path.Combine(AllocationDir, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Allocation fixture missing: {path}. {RegenerateHint}");
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static readonly Lazy<JsonElement> LazyManifest = new(LoadManifest);

    private static JsonElement Manifest => LazyManifest.Value;

    private static JsonElement LoadManifest()
    {
        JsonElement manifest = ReadJson("manifest.json");
        if (!manifest.TryGetProperty("fixtures", out JsonElement fixtures)
            || fixtures.ValueKind != JsonValueKind.Array
            || fixtures.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"The allocation manifest at {AllocationDir} lists no fixtures. An empty family " +
                $"guarantees nothing. {RegenerateHint}");
        }
        int declared = manifest.GetProperty("fixtureCount").GetInt32();
        if (declared != fixtures.GetArrayLength())
        {
            throw new InvalidOperationException(
                $"The allocation manifest says fixtureCount={declared} but lists " +
                $"{fixtures.GetArrayLength()} fixtures, so it was hand-edited. {RegenerateHint}");
        }
        if (manifest.GetProperty("contextsPerFixture").GetInt32() <= 0)
        {
            throw new InvalidOperationException(
                "The allocation manifest declares no contextsPerFixture.");
        }
        return manifest;
    }

    private static int ContextsPerFixture => Manifest.GetProperty("contextsPerFixture").GetInt32();

    private static JsonElement LoadFixture(string caseName, string fileName)
    {
        JsonElement fixture = ReadJson(fileName);
        string? declared = fixture.GetProperty("case").GetString();
        if (declared != caseName)
        {
            throw new InvalidOperationException(
                $"Allocation fixture {fileName} declares case '{declared}' but the manifest " +
                $"names it '{caseName}'.");
        }
        if (!fixture.TryGetProperty("evaluations", out JsonElement evaluations)
            || evaluations.ValueKind != JsonValueKind.Array
            || evaluations.GetArrayLength() != ContextsPerFixture)
        {
            throw new InvalidOperationException(
                $"Allocation fixture {fileName} does not carry {ContextsPerFixture} evaluations. " +
                RegenerateHint);
        }
        return fixture;
    }

    public static IEnumerable<object[]> AllocationFixtures() =>
        // Throwing here is deliberate: xUnit reports it as a failure, never as "nothing to test".
        Manifest.GetProperty("fixtures").EnumerateArray()
            .Select(entry => new object[]
            {
                entry.GetProperty("case").GetString()!,
                entry.GetProperty("file").GetString()!,
            });

    /// <summary>Replays every evaluation of one fixture; returns how many ran and what drifted.</summary>
    private static (int Ran, List<string> Drift) Replay(string caseName, string fileName)
    {
        JsonElement fixture = LoadFixture(caseName, fileName);
        string flagKey = fixture.GetProperty("flagKey").GetString()!;
        object? defaultValue = ConformanceFixtures.ParseDefault(fixture.GetProperty("defaultValue"));
        FlagDatafile datafile = InMemoryDatafileLoader.Load(fixture.GetProperty("datafile").GetRawText());

        var drift = new List<string>();
        int ran = 0;
        foreach (JsonElement evaluation in fixture.GetProperty("evaluations").EnumerateArray())
        {
            JsonElement contextJson = evaluation.GetProperty("context");
            JsonElement expected = evaluation.GetProperty("expected");
            // Reasons are part of the contract, so the harness always collects them.
            var reasons = new List<string>();
            Evaluator.EvalResult result = Evaluator.EvaluateFlag(
                flagKey, defaultValue, ConformanceFixtures.ParseContext(contextJson), datafile, reasons);
            ran++;

            string? expectedRuleType = expected.GetProperty("ruleType").GetString();
            List<string> expectedReasons = expected.GetProperty("reasons").EnumerateArray()
                .Select(r => r.GetString()!).ToList();
            bool same =
                ConformanceFixtures.JsonValuesEqual(expected.GetProperty("value"), result.Value)
                && result.VariationKey == expected.GetProperty("variationKey").GetString()
                && result.Source == ConformanceFixtures.ParseSource(expected.GetProperty("source").GetString()!)
                && result.RuleId == expected.GetProperty("ruleId").GetString()
                && result.RuleType == (expectedRuleType is null
                    ? (RuleType?)null
                    : ConformanceFixtures.ParseRuleType(expectedRuleType))
                && reasons.SequenceEqual(expectedReasons);
            if (!same)
            {
                drift.Add($"{contextJson.GetProperty("key").GetString()}: got {result.Source} "
                    + $"{result.RuleId ?? "null"} [{string.Join(" | ", reasons)}]");
            }
        }
        return (ran, drift);
    }

    [Fact]
    public void TheManifestNamesEveryFixtureOnDisk()
    {
        var named = Manifest.GetProperty("fixtures").EnumerateArray()
            .Select(entry => entry.GetProperty("file").GetString()!)
            .ToHashSet();
        var onDisk = Directory.EnumerateFiles(AllocationDir, "*.json")
            .Select(path => Path.GetFileName(path)!)
            .Where(name => name != "manifest.json")
            .ToHashSet();

        onDisk.Except(named).Should().BeEmpty("every allocation fixture must be named by the manifest");
        named.Except(onDisk).Should().BeEmpty("every manifest entry must have its file");
    }

    [Fact]
    public void TheFamilyCarriesTheSixRolloutLevels()
    {
        Manifest.GetProperty("fixtures").EnumerateArray()
            .Select(entry => entry.GetProperty("case").GetString())
            .Should().Equal(
                "targeted_delivery_allocation_0.05",
                "targeted_delivery_allocation_0.3",
                "targeted_delivery_allocation_0.5",
                "targeted_delivery_allocation_fractional_boundary",
                "targeted_delivery_allocation_1.0",
                "targeted_delivery_allocation_null");
    }

    [Theory]
    [MemberData(nameof(AllocationFixtures))]
    public void EveryRecordedEvaluationMatchesTheReference(string caseName, string fileName)
    {
        (int ran, List<string> drift) = Replay(caseName, fileName);

        ran.Should().Be(ContextsPerFixture);
        drift.Should().BeEmpty($"{caseName}: {drift.Count} contexts drifted from the reference");
    }

    [Fact]
    public void TheWholeFamilyRan_SoNothingCanPassByRunningNothing()
    {
        int total = AllocationFixtures().Sum(args => Replay((string)args[0], (string)args[1]).Ran);

        total.Should().Be(Manifest.GetProperty("fixtureCount").GetInt32() * ContextsPerFixture);
        total.Should().Be(1200);
    }

    [Fact]
    public void TheFractionalFixturePutsItsSearchedBoundaryKeysFirst()
    {
        JsonElement fractional = LoadFixture(
            "targeted_delivery_allocation_fractional_boundary",
            "targeted_delivery_allocation_fractional_boundary.json");
        var probes = fractional.GetProperty("probes").EnumerateArray().ToList();
        probes.Select(p => p.GetProperty("bucket").GetInt32()).Should().Equal(1234, 1235);

        var firstTwo = fractional.GetProperty("evaluations").EnumerateArray().Take(2).ToList();
        firstTwo.Select(e => e.GetProperty("context").GetProperty("key").GetString())
            .Should().Equal(probes.Select(p => p.GetProperty("resolvedKey").GetString()));
        // Bucket 1234 is inside the 1234.5 threshold; a port that floors it would say no.
        firstTwo.Select(e => e.GetProperty("expected").GetProperty("source").GetString())
            .Should().Equal("rule", "default");
    }
}
