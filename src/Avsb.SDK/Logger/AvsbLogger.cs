using Microsoft.Extensions.Logging;

namespace Avsb.SDK.Logger;

/// <summary>
/// Thin wrapper that routes SDK-internal log calls through an
/// Microsoft.Extensions.Logging.ILogger.
/// </summary>
public sealed class AvsbLogger
{
    private readonly ILogger? _inner;

    public AvsbLogger(ILogger? inner) => _inner = inner;

    public void Debug(string message, Exception? ex = null)
    {
        if (_inner is null) return;
        if (ex is null) _inner.LogDebug("{Message}", message);
        else _inner.LogDebug(ex, "{Message}", message);
    }

    public void Info(string message, Exception? ex = null)
    {
        if (_inner is null) return;
        if (ex is null) _inner.LogInformation("{Message}", message);
        else _inner.LogInformation(ex, "{Message}", message);
    }

    public void Warn(string message, Exception? ex = null)
    {
        if (_inner is null) return;
        if (ex is null) _inner.LogWarning("{Message}", message);
        else _inner.LogWarning(ex, "{Message}", message);
    }

    public void Error(string message, Exception? ex = null)
    {
        if (_inner is null) return;
        if (ex is null) _inner.LogError("{Message}", message);
        else _inner.LogError(ex, "{Message}", message);
    }
}
