using System.Net;
using System.Net.Http;
using Avsb.SDK;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// AvsbServer facade tests. We test the evaluation surface by injecting a
/// pre-loaded datafile via reflection (bypassing the HTTP fetch) so these
/// tests work without a network.
/// </summary>
public sealed class AvsbServerTests
{
    private static readonly string FixturesRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "avsb-conformance", "fixtures"));

    private static AvsbServer BuildServerWithDatafile(string fixtureName)
    {
        var df = InMemoryDatafileLoader.LoadFile(
            Path.Combine(FixturesRoot, "datafiles", $"{fixtureName}.datafile.json"));

        var server = new AvsbServer(new AvsbServerOptions { SdkKey = "test" });
        // Inject the datafile via the internal field using reflection.
        var field = typeof(AvsbServer).GetField("_datafile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(server, df);
        return server;
    }

    [Fact]
    public void GetBoolFlag_NotFound_ReturnsDefault()
    {
        var server = BuildServerWithDatafile("flag_not_found_returns_default");
        var flag = server.GetBoolFlag("does-not-exist", false, EvalContext.User("alice"));
        flag.Value.Should().BeFalse();
        flag.Source.Should().Be(EvaluationSource.NotFound);
        flag.Exists().Should().BeFalse();
    }

    [Fact]
    public void GetBoolFlag_DisabledFlag_ReturnsDisabledSource()
    {
        var server = BuildServerWithDatafile("disabled_flag_returns_default_variation_value");
        // The fixture flag key is "feature-off"
        var flag = server.GetBoolFlag("feature-off", true, EvalContext.User("any_user"));
        flag.Source.Should().Be(EvaluationSource.Disabled);
        flag.IsEnabled().Should().BeFalse();
    }

    [Fact]
    public void GetStringFlag_AbTest_ReturnsDeterministicValue()
    {
        var server = BuildServerWithDatafile("ab_test_bucket_deterministic_user_a");
        var flag = server.GetStringFlag("checkout-v2-experiment", "fallback", EvalContext.User("alice"));
        flag.Source.Should().Be(EvaluationSource.Rule);
        flag.VariationKey.Should().Be("control");
        flag.Value.Should().Be("old");
    }

    [Fact]
    public void DecisionLogEntries_Populated_AfterEvaluation()
    {
        var server = BuildServerWithDatafile("flag_not_found_returns_default");
        server.GetBoolFlag("does-not-exist", false, EvalContext.User("alice"));
        server.DecisionLogEntries.Should().HaveCount(1);
        server.DecisionLogEntries[0].FlagKey.Should().Be("does-not-exist");
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var server = new AvsbServer(new AvsbServerOptions { SdkKey = "test" });
        Func<Task> act = async () => await server.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
