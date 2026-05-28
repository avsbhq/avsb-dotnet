namespace Avsb.SDK;

/// <summary>
/// Unified evaluation return for a single flag lookup. Mirrors Flag&lt;T&gt; (§2.2).
/// </summary>
public readonly record struct Flag<T>
{
    /// <summary>The variation value typed against the default.</summary>
    public T Value { get; init; }

    /// <summary>Variation key; null when served the default or flag not found.</summary>
    public string? VariationKey { get; init; }

    /// <summary>Why this value was produced.</summary>
    public EvaluationSource Source { get; init; }

    /// <summary>Rule that matched; null when no rule applied.</summary>
    public string? RuleId { get; init; }

    /// <summary>Rule type that matched; null when no rule applied.</summary>
    public RuleType? RuleType { get; init; }

    /// <summary>Structured reasons for this decision.</summary>
    public IReadOnlyList<string> Reasons { get; init; }

    /// <summary>UTC ticks (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds) when evaluated.</summary>
    public long EvaluatedAt { get; init; }

    /// <summary>Microseconds elapsed inside the evaluator.</summary>
    public long DurationMicros { get; init; }

    /// <summary>
    /// Convenience: served by a rule and the value is truthy (non-null, non-false, non-zero).
    /// </summary>
    public bool IsEnabled()
    {
        if (Source is EvaluationSource.Default or EvaluationSource.NotFound or EvaluationSource.Disabled)
            return false;
        return Value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            string s => !string.IsNullOrEmpty(s),
            null => false,
            _ => true,
        };
    }

    /// <summary>Convenience: source is not NotFound.</summary>
    public bool Exists() => Source != EvaluationSource.NotFound;
}
