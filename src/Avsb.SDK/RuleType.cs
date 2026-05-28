namespace Avsb.SDK;

/// <summary>
/// Discriminates which rule type produced the flag decision. Mirrors the
/// wire spec RuleType union (§2.7).
/// </summary>
public enum RuleType
{
    TargetedDelivery,
    AbTest,
    Holdout,
    Bandit,
}
