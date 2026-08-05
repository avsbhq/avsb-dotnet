namespace Avsb.SDK.Logger;

/// <summary>
/// Discards every message. Use it to silence the SDK completely, and in tests.
///
/// Note that this is NOT what you get by leaving AvsbServerOptions.Logger unset: that gives you
/// <see cref="AvsbLogger"/>, which still writes warnings and errors to standard error, because
/// silence is what hid every failure in the previous generation of these SDKs.
/// </summary>
public sealed class NullLogger : AvsbLogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger() : base(null) { }

    public override void Debug(string message, Exception? ex = null) { }

    public override void Info(string message, Exception? ex = null) { }

    public override void Warn(string message, Exception? ex = null) { }

    public override void Error(string message, Exception? ex = null) { }
}
