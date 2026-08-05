namespace Avsb.SDK;

/// <summary>
/// Why a particular flag value was produced. Mirrors the EvaluationSource union in types.ts and
/// the sources.public list in the conformance corpus' semantics fixture family.
///
/// These ten members are the complete public set. Two spellings the internal evaluator once used,
/// "override" and "runtime_override", are mapped at the SDK boundary and never reach a caller.
/// </summary>
public enum EvaluationSource
{
    /// <summary>Value overridden in the datafile for a specific user id.</summary>
    DatafileOverride,

    /// <summary>Value overridden at runtime through AvsbServerOptions.RuntimeOverrides.</summary>
    RuntimeOverride,

    /// <summary>Value pinned by a stored sticky-bucket assignment.</summary>
    Sticky,

    /// <summary>Value served by a matching targeted-delivery or AB-test rule.</summary>
    Rule,

    /// <summary>Context is inside a holdout window; served the holdout's variation.</summary>
    Holdout,

    /// <summary>Value selected by the bandit engine.</summary>
    Bandit,

    /// <summary>No rule matched; served the flag's default variation.</summary>
    Default,

    /// <summary>Flag key is not present in the datafile.</summary>
    NotFound,

    /// <summary>Flag is disabled; served the flag's default variation.</summary>
    Disabled,

    /// <summary>
    /// There was no datafile to look in: InitAsync has not completed, the fetch failed with no
    /// bootstrap, or the client was disposed.
    ///
    /// Distinct from <see cref="NotFound"/>, and the distinction is the point. Reporting "flag
    /// does not exist" for an SDK that never started sent people hunting for a missing flag in
    /// the dashboard instead of looking at their init call. Every language port used to do it.
    /// </summary>
    NotReady,
}

/// <summary>Wire spellings and the two source groups the semantics contract defines.</summary>
public static class EvaluationSourceExtensions
{
    /// <summary>
    /// The six sources that mean "a real decision produced this value". Everything else is a
    /// fallback, and IsEnabled is false for all of them however truthy the value looks.
    /// </summary>
    public static bool IsDecided(this EvaluationSource source) => source
        is EvaluationSource.Rule
        or EvaluationSource.Holdout
        or EvaluationSource.Bandit
        or EvaluationSource.RuntimeOverride
        or EvaluationSource.DatafileOverride
        or EvaluationSource.Sticky;

    /// <summary>Sources that mean "no datafile answer for this key".</summary>
    public static bool IsUnknownFlag(this EvaluationSource source) =>
        source is EvaluationSource.NotFound or EvaluationSource.NotReady;

    /// <summary>Wire-format name, as it appears in fixtures, API responses, and decision logs.</summary>
    public static string WireValue(this EvaluationSource source) => source switch
    {
        EvaluationSource.DatafileOverride => "datafileOverride",
        EvaluationSource.RuntimeOverride => "runtimeOverride",
        EvaluationSource.Sticky => "sticky",
        EvaluationSource.Rule => "rule",
        EvaluationSource.Holdout => "holdout",
        EvaluationSource.Bandit => "bandit",
        EvaluationSource.Default => "default",
        EvaluationSource.NotFound => "not_found",
        EvaluationSource.Disabled => "disabled",
        EvaluationSource.NotReady => "not_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "unmapped source"),
    };

    /// <summary>
    /// Parse a wire-format string. Throws for a spelling this SDK does not know: falling back to
    /// NotFound turned "this SDK has never heard of that source" into a silently wrong answer,
    /// including inside the conformance harness.
    /// </summary>
    public static EvaluationSource FromWire(string wire) => wire switch
    {
        "datafileOverride" => EvaluationSource.DatafileOverride,
        "runtimeOverride" => EvaluationSource.RuntimeOverride,
        "sticky" => EvaluationSource.Sticky,
        "rule" => EvaluationSource.Rule,
        "holdout" => EvaluationSource.Holdout,
        "bandit" => EvaluationSource.Bandit,
        "default" => EvaluationSource.Default,
        "not_found" => EvaluationSource.NotFound,
        "disabled" => EvaluationSource.Disabled,
        "not_ready" => EvaluationSource.NotReady,
        // The internal evaluator's legacy spellings, mapped at the boundary.
        "override" => EvaluationSource.DatafileOverride,
        "runtime_override" => EvaluationSource.RuntimeOverride,
        _ => throw new ArgumentException(
            $"Unknown evaluation source \"{wire}\". The public set is listed in the conformance"
            + " corpus at fixtures/semantics/manifest.json.", nameof(wire)),
    };
}
