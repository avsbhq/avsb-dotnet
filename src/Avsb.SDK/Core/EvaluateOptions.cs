namespace Avsb.SDK.Core;

/// <summary>
/// Optional inputs to <see cref="Evaluator.EvaluateFlag"/>.
///
/// Kept as one value rather than more positional parameters so adding an input later cannot
/// silently reorder an existing call.
/// </summary>
public sealed record EvaluateOptions
{
    /// <summary>
    /// Called once per served ab_test, bandit, or holdout decision. Null suppresses exposure
    /// recording, which is what a preview evaluation wants.
    /// </summary>
    public Action<ExposureEvent>? OnExposure { get; init; }

    /// <summary>
    /// Learned bandit models. Null means the evaluator builds a deterministic cold-start model
    /// from the datafile's bandit config alone, so a bandit rule dispatches instead of
    /// disappearing.
    /// </summary>
    public IAvsbBanditModelStore? BanditModelStore { get; init; }

    /// <summary>No exposure recording and no learned models.</summary>
    public static readonly EvaluateOptions None = new();
}
