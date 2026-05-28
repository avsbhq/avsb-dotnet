using Microsoft.Extensions.DependencyInjection;

namespace Avsb.SDK.AspNetCore;

/// <summary>
/// IServiceCollection extension that registers AvsbServer as a singleton
/// and wires up the request-scoped context middleware.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAvsb(
        this IServiceCollection services,
        Action<AvsbServerOptions> configure)
    {
        var options = new AvsbServerOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<AvsbServer>();
        services.AddSingleton<AvsbMiddleware>();

        return services;
    }
}
