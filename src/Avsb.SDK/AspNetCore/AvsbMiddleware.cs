using Microsoft.AspNetCore.Http;

namespace Avsb.SDK.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that binds an EvalContext to the current
/// HttpContext.Items so route handlers can call GetAvsbContext() without
/// constructing a context manually on every request.
/// </summary>
public sealed class AvsbMiddleware : IMiddleware
{
    private const string ContextKey = "avsb.eval_context";

    /// <summary>
    /// Default context resolver: builds a user context from the
    /// HttpContext.User.Identity.Name claim. Override by setting
    /// HttpContext.Items[AvsbMiddleware.ContextKey] before this middleware runs.
    /// </summary>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Items.ContainsKey(ContextKey))
        {
            var userId = context.User?.Identity?.Name ?? string.Empty;
            context.Items[ContextKey] = EvalContext.User(userId);
        }
        await next(context).ConfigureAwait(false);
    }

    internal static string ItemKey => ContextKey;
}
