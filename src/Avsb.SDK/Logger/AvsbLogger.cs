using Microsoft.Extensions.Logging;

namespace Avsb.SDK.Logger;

/// <summary>
/// Routes SDK-internal log calls through a Microsoft.Extensions.Logging ILogger.
///
/// When no ILogger is configured this does NOT go silent. It writes warnings and errors to
/// standard error, and drops debug and info. That is deliberate: every diagnosable failure in the
/// previous generation of these SDKs (wrong key, 404 datafile, ignored option, dropped event) was
/// whispered into a logger nobody had configured, so the symptom people actually saw was "flags
/// always return the default" with nothing to go on.
///
/// It falls back to silence only when the process declares itself production through
/// ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT, or AVSB_ENV.
/// </summary>
public class AvsbLogger
{
    private readonly ILogger? _inner;
    private readonly bool _consoleFallback;

    public AvsbLogger(ILogger? inner)
    {
        _inner = inner;
        _consoleFallback = inner is null && !IsProduction();
    }

    /// <summary>True when an environment variable names this a production process.</summary>
    public static bool IsProduction()
    {
        foreach (string name in new[] { "AVSB_ENV", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null
                && value.Contains("prod", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public virtual void Debug(string message, Exception? ex = null)
    {
        if (_inner is null) return;
        if (ex is null) _inner.LogDebug("{Message}", message);
        else _inner.LogDebug(ex, "{Message}", message);
    }

    public virtual void Info(string message, Exception? ex = null)
    {
        if (_inner is null) return;
        if (ex is null) _inner.LogInformation("{Message}", message);
        else _inner.LogInformation(ex, "{Message}", message);
    }

    public virtual void Warn(string message, Exception? ex = null)
    {
        if (_inner is null)
        {
            if (_consoleFallback) Console.Error.WriteLine("[AVSB WARN]  " + Compose(message, ex));
            return;
        }
        if (ex is null) _inner.LogWarning("{Message}", message);
        else _inner.LogWarning(ex, "{Message}", message);
    }

    public virtual void Error(string message, Exception? ex = null)
    {
        if (_inner is null)
        {
            if (_consoleFallback) Console.Error.WriteLine("[AVSB ERROR] " + Compose(message, ex));
            return;
        }
        if (ex is null) _inner.LogError("{Message}", message);
        else _inner.LogError(ex, "{Message}", message);
    }

    private static string Compose(string message, Exception? ex) =>
        ex is null ? message : message + " " + ex.Message;
}
