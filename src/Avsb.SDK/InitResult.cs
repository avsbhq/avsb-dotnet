namespace Avsb.SDK;

/// <summary>Result of AvsbServer.InitAsync. Mirrors §2.3.</summary>
public sealed record InitResult
{
    public bool Success { get; init; }
    public InitResultSource Source { get; init; }
    public Exception? Error { get; init; }

    /// <summary>
    /// SDK is functional on defaults; polling may still deliver a fresh datafile.
    /// </summary>
    public bool Degraded { get; init; }
}

public enum InitResultSource
{
    Network,
    Bootstrap,
    Timeout,
    Error,
}
