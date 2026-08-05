using Avsb.SDK.Core;

namespace Avsb.SDK;

/// <summary>
/// The unified evaluation result. Mirrors Flag&lt;T&gt; in types.ts.
/// </summary>
/// <typeparam name="T">
/// The resolved value type: bool, string, double, or whatever a JSON flag was asked to
/// deserialise into.
/// </typeparam>
public readonly record struct Flag<T>
{
    /// <summary>The variation value, or your default when the datafile could not answer.</summary>
    public T Value { get; init; }

    /// <summary>Variation key; null when no variation was served.</summary>
    public string? VariationKey { get; init; }

    /// <summary>Why this value was produced. Never null.</summary>
    public EvaluationSource Source { get; init; }

    /// <summary>Rule that matched; null unless a rule or holdout decided.</summary>
    public string? RuleId { get; init; }

    /// <summary>Rule type that matched; null unless a rule or holdout decided.</summary>
    public RuleType? RuleType { get; init; }

    /// <summary>Ordered reason strings for this decision. Never null.</summary>
    public IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Milliseconds since the Unix epoch when this was evaluated.</summary>
    public long EvaluatedAt { get; init; }

    /// <summary>Microseconds elapsed inside the evaluator.</summary>
    public long DurationMicros { get; init; }

    /// <summary>
    /// True when a real decision produced this value AND the value is truthy.
    ///
    /// Two halves, both fixture-locked (spec section 1.4):
    ///
    /// 1. The source must be one of rule, holdout, bandit, runtimeOverride, datafileOverride, or
    ///    sticky. A flag serving its DEFAULT variation is not enabled, however truthy that
    ///    default is. Neither is a disabled flag, a missing flag, or an SDK that has not
    ///    initialised.
    /// 2. Truthiness follows JAVASCRIPT rules, not .NET's, because the reference engine is
    ///    TypeScript and the answer has to be identical in all eight implementations: false, 0,
    ///    -0, NaN, the empty string, and null are falsy; everything else is truthy, INCLUDING the
    ///    string "false", the string "0", an empty array, and an empty object.
    /// </summary>
    public bool IsEnabled() => Source.IsDecided() && JsNumber.IsTruthy(Value);

    /// <summary>
    /// True when the datafile has an answer about this key.
    ///
    /// False for not_found and for not_ready: with no datafile loaded, whether the key exists is
    /// unknowable, so the honest answer is false rather than a guess. A DISABLED flag exists: the
    /// customer turned it off, which is not the same as it being absent.
    /// </summary>
    public bool Exists() => !Source.IsUnknownFlag();

    /// <summary>
    /// The result of evaluating before a datafile existed: the caller's default value with source
    /// NotReady, no variation, and one reason naming the state (spec section 1.2).
    /// </summary>
    public static Flag<T> NotReady(T defaultValue, string reason) => new()
    {
        Value = defaultValue,
        Source = EvaluationSource.NotReady,
        Reasons = new[] { reason },
        EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    /// <summary>
    /// The result of asking for a key the loaded datafile does not carry, or of a typed getter
    /// whose declared type disagreed with the resolved value.
    /// </summary>
    public static Flag<T> NotFound(T defaultValue, string reason) => new()
    {
        Value = defaultValue,
        Source = EvaluationSource.NotFound,
        Reasons = new[] { reason },
        EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
}
