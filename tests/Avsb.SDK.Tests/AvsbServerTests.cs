using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Avsb.SDK;
using Avsb.SDK.AspNetCore;
using Avsb.SDK.Core;
using Avsb.SDK.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Avsb.SDK.Tests;

/// <summary>
/// AvsbServer facade tests. We test the evaluation surface by injecting a
/// pre-loaded datafile via reflection (bypassing the HTTP fetch) so these
/// tests work without a network.
/// </summary>
public sealed class AvsbServerTests
{
    // Resolved once, by walking upward to the corpus (see ConformanceFixtures): a fixed count of
    // ".." segments from the build output landed one directory short.
    private static string FixturesRoot => ConformanceFixtures.Root;

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

    // -----------------------------------------------------------------------
    // Synchronous disposal (FU-40). AvsbServer was only IAsyncDisposable, so `using var` did not
    // compile and a container disposed with Dispose() threw for the singleton.
    // -----------------------------------------------------------------------

    private const string OfflineDatafile = """
        {
          "version": 2,
          "sdkKey": "sdk_test_conformancefixture01",
          "flags": [
            {
              "id": "f1", "key": "bool-flag", "type": "boolean", "enabled": true,
              "defaultVariationId": "var_on",
              "variations": [{"id": "var_on", "key": "on", "value": true}],
              "overrides": [], "rules": []
            }
          ]
        }
        """;

    private static AvsbServerOptions OfflineOptions() => new()
    {
        SdkKey = "sdk_test_conformancefixture01",
        Bootstrap = OfflineDatafile,
        Polling = false,
        Tracking = false,
    };

    [Fact]
    public void UsingVar_DisposesTheServerSynchronously()
    {
        AvsbServer captured;
        {
            using var server = new AvsbServer(OfflineOptions());
            server.GetBoolFlag("bool-flag", false, EvalContext.User("alice")).Value.Should().BeTrue();
            captured = server;
        }

        // Disposed: the datafile is gone and evaluation says so instead of serving stale values.
        captured.GetBoolFlag("bool-flag", false, EvalContext.User("alice")).Source
            .Should().Be(EvaluationSource.NotReady);
    }

    [Fact]
    public async Task DisposeThenDisposeAsync_IsANoOpTheSecondTime()
    {
        var server = new AvsbServer(OfflineOptions());
        server.Dispose();

        Func<Task> again = async () => await server.DisposeAsync();
        await again.Should().NotThrowAsync();
        server.Invoking(s => s.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsyncThenDispose_IsANoOpTheSecondTime()
    {
        var server = new AvsbServer(OfflineOptions());
        await server.DisposeAsync();

        server.Invoking(s => s.Dispose()).Should().NotThrow();
        Func<Task> again = async () => await server.DisposeAsync();
        await again.Should().NotThrowAsync();
    }

    [Fact]
    public void AContainerDisposedSynchronously_DisposesTheSingletonWithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddAvsb(options =>
        {
            options.SdkKey = "sdk_test_conformancefixture01";
            options.Bootstrap = OfflineDatafile;
            options.Polling = false;
            options.Tracking = false;
        });
        ServiceProvider provider = services.BuildServiceProvider();
        AvsbServer server = provider.GetRequiredService<AvsbServer>();

        // Before AvsbServer implemented IDisposable this threw InvalidOperationException: the
        // container refuses to dispose an IAsyncDisposable-only singleton synchronously.
        provider.Invoking(p => p.Dispose()).Should().NotThrow();
        server.GetBoolFlag("bool-flag", false, EvalContext.User("alice")).Source
            .Should().Be(EvaluationSource.NotReady);
    }

    [Fact]
    public async Task Dispose_FlushesQueuedEventsBeforeReturning()
    {
        int port = FreeLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ServeOneAsync(listener, received);

        string datafile = OfflineDatafile.Replace(
            "\"flags\":", $"\"collectEndpoint\": \"http://127.0.0.1:{port}\", \"flags\":");
        var server = new AvsbServer(new AvsbServerOptions
        {
            SdkKey = "sdk_test_conformancefixture01",
            Bootstrap = datafile,
            Polling = false,
            Tracking = true,
        });
        server.Track("signup_completed", EvalContext.User("alice"));

        server.Dispose();

        received.Task.IsCompleted.Should().BeTrue("a synchronous Dispose sends the queued batch before it returns");
        string path = await received.Task;
        path.Should().Be(Endpoints.CollectPathMetricBatch);
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Records the request path BEFORE answering, so the path is known by the time the sender
    // sees the response.
    private static async Task ServeOneAsync(HttpListener listener, TaskCompletionSource<string> seen)
    {
        HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
        seen.TrySetResult(context.Request.Url!.AbsolutePath);
        context.Response.StatusCode = 202;
        context.Response.Close();
    }
}
