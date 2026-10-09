using Avsb.SDK.AspNetCore;
using Avsb.SDK.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Avsb.SDK.Tests;

/// <summary>
/// The options fixes that came with making the SDK compile (N0-6).
///
/// The README's ASP.NET Core registration, <c>AddAvsb(options => options.SdkKey = ...)</c>, was a
/// compile error because every option was init-only. The options are now settable, and the server
/// copies them at construction so a caller that keeps the object cannot change a running server.
/// </summary>
public sealed class AvsbServerOptionsTests
{
    private const string SdkKey = "sdk_test_conformancefixture01";

    private static string AbDatafile() =>
        File.ReadAllText(ConformanceFixtures.DatafilePath("ab_test_bucket_deterministic_user_a"));

    [Fact]
    public async Task AddAvsb_AcceptsTheDocumentedOptionsLambda()
    {
        var services = new ServiceCollection();
        services.AddAvsb(options =>
        {
            options.SdkKey = SdkKey;
            options.Polling = false;
            options.Tracking = false;
        });

        // AvsbServer is IAsyncDisposable only, so the container is disposed asynchronously, as the
        // ASP.NET Core host does.
        await using ServiceProvider provider = services.BuildServiceProvider();
        AvsbServerOptions registered = provider.GetRequiredService<AvsbServerOptions>();
        registered.SdkKey.Should().Be(SdkKey);
        registered.Polling.Should().BeFalse();
        provider.GetRequiredService<AvsbServer>().Should().NotBeNull();
    }

    [Fact]
    public void Server_KeepsTheOptionsItWasBuiltWith_WhenTheCallerMutatesThemLater()
    {
        var options = new AvsbServerOptions
        {
            SdkKey = SdkKey,
            Bootstrap = AbDatafile(),
            Polling = false,
            Tracking = false,
            RuntimeOverrides = new Dictionary<string, string> { ["checkout-v2-experiment"] = "treatment" },
        };
        var server = new AvsbServer(options);

        options.RuntimeOverrides = null;

        Flag<string> flag = server.GetStringFlag("checkout-v2-experiment", "fallback",
            EvalContext.User("alice"));
        flag.Source.Should().Be(EvaluationSource.RuntimeOverride);
        flag.VariationKey.Should().Be("treatment");
    }

    [Fact]
    public void ObjectInitializersAndWithExpressionsStillWork()
    {
        var original = new AvsbServerOptions { SdkKey = SdkKey, Polling = false };
        AvsbServerOptions copy = original with { CdnHost = "https://mirror.example" };

        copy.SdkKey.Should().Be(SdkKey);
        copy.CdnHost.Should().Be("https://mirror.example");
        original.CdnHost.Should().BeNull("a with-expression copies; it never edits the source");
    }

    [Fact]
    public void PrivateAttributes_AreCollectedFromEveryKind_CaseInsensitively()
    {
        var ctx = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["user"] = new SingleContext
                {
                    Kind = "user",
                    Key = "alice",
                    Meta = new ContextMeta { PrivateAttributes = new[] { "email" } },
                },
                ["organization"] = new SingleContext
                {
                    Kind = "organization",
                    Key = "acme",
                    Meta = new ContextMeta { PrivateAttributes = new[] { "Revenue" } },
                },
            },
        });

        IReadOnlyCollection<string> collected = ContextHelper.PrivateAttributesFrom(ctx);

        collected.Should().HaveCount(2);
        collected.Should().Contain(new[] { "email", "Revenue" });
        DecisionLog.Redact(
                new DecisionLogEntry { FlagKey = "f", Reasons = new[] { "matched REVENUE above 10" } },
                collected)
            .Reasons.Should().Equal("matched [REDACTED] above 10");
    }
}
