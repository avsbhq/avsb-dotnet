// NetStandardCompat.cs
// The few framework APIs this SDK uses on net8.0 that netstandard2.1 does not have. Each one
// resolves to the framework's own member where it exists, so net8.0 behaviour is unchanged; the
// netstandard2.1 build gets the closest equivalent instead of failing to compile.

namespace Avsb.SDK;

/// <summary>
/// A thread-safe source of jitter. <c>Random.Shared</c> arrived in .NET 6; below that each thread
/// gets its own instance, seeded apart so threads that start together do not jitter in lockstep.
/// </summary>
internal static class SharedRandom
{
#if NET6_0_OR_GREATER
    /// <summary>A double in [0, 1).</summary>
    public static double NextDouble() => Random.Shared.NextDouble();
#else
    [ThreadStatic] private static Random? _local;
    private static int _seed = Environment.TickCount;

    /// <summary>A double in [0, 1).</summary>
    public static double NextDouble() =>
        (_local ??= new Random(Interlocked.Increment(ref _seed))).NextDouble();
#endif
}

#if !NET5_0_OR_GREATER
/// <summary>
/// The CancellationToken overloads of HttpContent's read methods arrived in .NET 5. On
/// netstandard2.1 these extensions stand in for them (an instance method always wins where the
/// framework has one): the token is honoured before the read starts, and the request itself was
/// already sent with the same token.
/// </summary>
internal static class HttpContentCompat
{
    public static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return content.ReadAsStringAsync();
    }

    public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return content.ReadAsStreamAsync();
    }
}
#endif
