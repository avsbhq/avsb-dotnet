namespace Avsb.SDK;

/// <summary>
/// Describes why a flag value was produced. Mirrors §2.12 of the wire spec.
/// </summary>
public enum EvaluationSource
{
    DatafileOverride,
    RuntimeOverride,
    Sticky,
    Rule,
    Holdout,
    Bandit,
    Default,
    NotFound,
    Disabled,
}
