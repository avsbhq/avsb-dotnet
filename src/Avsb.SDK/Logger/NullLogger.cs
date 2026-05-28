namespace Avsb.SDK.Logger;

/// <summary>No-op logger used when no ILogger is configured.</summary>
public sealed class NullLogger : AvsbLogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() : base(null) { }
}
