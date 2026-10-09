using Avsb.SDK;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;
using OpenFeature.Model;

namespace Avsb.SDK.Tests;

public sealed class OpenFeatureProviderTests
{
    // Resolved once, by walking upward to the corpus (see ConformanceFixtures): a fixed count of
    // ".." segments from the build output landed one directory short.
    private static string FixturesRoot => ConformanceFixtures.Root;

    private static AvsbServer BuildServer(string fixtureName)
    {
        var df = InMemoryDatafileLoader.LoadFile(
            Path.Combine(FixturesRoot, "datafiles", $"{fixtureName}.datafile.json"));
        var server = new AvsbServer(new AvsbServerOptions { SdkKey = "test" });
        var field = typeof(AvsbServer).GetField("_datafile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(server, df);
        return server;
    }

    [Fact]
    public void GetMetadata_ReturnsAvsbName()
    {
        var provider = new AvsbOpenFeatureProvider(BuildServer("flag_not_found_returns_default"));
        provider.GetMetadata().Name.Should().Be("AvsB");
    }

    [Fact]
    public async Task ResolveBooleanValueAsync_NotFound_ReturnsDefault()
    {
        var provider = new AvsbOpenFeatureProvider(BuildServer("flag_not_found_returns_default"));
        var ctx = EvaluationContext.Builder().SetTargetingKey("alice").Build();
        var result = await provider.ResolveBooleanValueAsync("does-not-exist", false, ctx);
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveStringValueAsync_AbTest_ReturnsDeterministicValue()
    {
        var provider = new AvsbOpenFeatureProvider(BuildServer("ab_test_bucket_deterministic_user_a"));
        var ctx = EvaluationContext.Builder().SetTargetingKey("alice").Build();
        var result = await provider.ResolveStringValueAsync("checkout-v2-experiment", "fallback", ctx);
        result.Value.Should().Be("old");
    }

    [Fact]
    public async Task ResolveDoubleValueAsync_NotFound_ReturnsDefault()
    {
        var provider = new AvsbOpenFeatureProvider(BuildServer("flag_not_found_returns_default"));
        var ctx = EvaluationContext.Builder().SetTargetingKey("alice").Build();
        var result = await provider.ResolveDoubleValueAsync("no-flag", 3.14, ctx);
        result.Value.Should().Be(3.14);
    }
}
