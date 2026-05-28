using Microsoft.AspNetCore.Http;

namespace Avsb.SDK.AspNetCore;

/// <summary>
/// Convenience extensions for retrieving the AvsB eval context bound by
/// AvsbMiddleware to the current HTTP request.
/// </summary>
public static class AvsbHttpContextExtensions
{
    /// <summary>
    /// Returns the EvalContext bound by AvsbMiddleware, or a default
    /// anonymous user context when the middleware was not registered.
    /// </summary>
    public static EvalContext GetAvsbContext(this HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(AvsbMiddleware.ItemKey, out var raw)
            && raw is EvalContext ctx)
        {
            return ctx;
        }
        return EvalContext.User(string.Empty);
    }

    /// <summary>
    /// Override the EvalContext for the current request (e.g. after
    /// resolving the authenticated user in a custom middleware).
    /// </summary>
    public static void SetAvsbContext(this HttpContext httpContext, EvalContext ctx)
    {
        httpContext.Items[AvsbMiddleware.ItemKey] = ctx;
    }
}
