using Avsb.SDK.AspNetCore;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Avsb.SDK.Tests;

public sealed class AspNetCoreMiddlewareTests
{
    [Fact]
    public async Task Middleware_SetsDefaultAnonymousContext_WhenNoUser()
    {
        EvalContext? captured = null;

        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddAvsb(o => o.SdkKey = "test");
                });
                webBuilder.Configure(app =>
                {
                    app.UseMiddleware<AvsbMiddleware>();
                    app.Run(ctx =>
                    {
                        captured = ctx.GetAvsbContext();
                        return Task.CompletedTask;
                    });
                });
            })
            .StartAsync();

        await host.GetTestClient().GetAsync("/");
        captured.Should().NotBeNull();
        captured!.Single.Should().NotBeNull();
    }

    [Fact]
    public async Task SetAvsbContext_Overrides_MiddlewareDefault()
    {
        EvalContext? captured = null;

        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddAvsb(o => o.SdkKey = "test");
                });
                webBuilder.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.SetAvsbContext(EvalContext.User("custom-user"));
                        await next(ctx);
                    });
                    app.UseMiddleware<AvsbMiddleware>();
                    app.Run(ctx =>
                    {
                        captured = ctx.GetAvsbContext();
                        return Task.CompletedTask;
                    });
                });
            })
            .StartAsync();

        await host.GetTestClient().GetAsync("/");
        captured!.Single!.Key.Should().Be("custom-user");
    }

    [Fact]
    public async Task GetAvsbContext_ReturnsAnonymous_WhenMiddlewareNotUsed()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.Configure(app =>
                {
                    app.Run(ctx =>
                    {
                        var evalCtx = ctx.GetAvsbContext();
                        evalCtx.Single!.Key.Should().Be(string.Empty);
                        return Task.CompletedTask;
                    });
                });
            })
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
