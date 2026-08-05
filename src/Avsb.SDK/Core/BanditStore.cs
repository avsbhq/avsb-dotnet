namespace Avsb.SDK.Core;

/// <summary>
/// Learned bandit models. Models are client-layer state (they are learned, not published in the
/// datafile), so the SDK takes a store rather than reading the datafile.
///
/// Public because a caller configures one through <c>AvsbServerOptions.BanditModelStore</c>; the
/// model types themselves live on <see cref="BanditEvaluator"/>.
/// </summary>
public interface IAvsbBanditModelStore
{
    /// <summary>Synchronous hot-path read. Null means "no model for this bandit".</summary>
    BanditEvaluator.BanditModel? GetModel(string banditKey);

    /// <summary>Fire and forget: implementations may batch or persist asynchronously.</summary>
    void UpdateModel(string banditKey, BanditEvaluator.BanditModel model);
}

/// <summary>
/// A store that hands back a zero-observation model for any bandit key, so the algorithms run
/// their documented cold-start policies rather than the rule being skipped. This is what the
/// conformance corpus locks: the same datafile and context must produce the same action in every
/// SDK with no model present.
/// </summary>
internal sealed class ColdStartBanditStore : IAvsbBanditModelStore
{
    /// <summary>Model version reported for the store-free cold-start fallback.</summary>
    public const string ColdStartModelVersion = "cold-start";

    private readonly BanditEvaluator.BanditModel _model;

    public ColdStartBanditStore(BanditConfig config)
    {
        _model = new BanditEvaluator.BanditModel
        {
            Version = ColdStartModelVersion,
            Algorithm = config.Algorithm,
            PerAction = new Dictionary<string, BanditEvaluator.PerActionStats>(),
        };
    }

    public BanditEvaluator.BanditModel? GetModel(string banditKey) => _model;

    public void UpdateModel(string banditKey, BanditEvaluator.BanditModel model)
    {
        // Nothing to persist: the cold-start model is derived, not stored.
    }
}

/// <summary>In-memory model store, for tests and single-process deployments.</summary>
public sealed class InMemoryBanditModelStore : IAvsbBanditModelStore
{
    private readonly Dictionary<string, BanditEvaluator.BanditModel> _models;
    private readonly object _lock = new();

    public InMemoryBanditModelStore(
        IReadOnlyDictionary<string, BanditEvaluator.BanditModel>? initial = null)
    {
        _models = initial is null
            ? new Dictionary<string, BanditEvaluator.BanditModel>()
            : new Dictionary<string, BanditEvaluator.BanditModel>(initial);
    }

    public BanditEvaluator.BanditModel? GetModel(string banditKey)
    {
        lock (_lock)
        {
            return _models.TryGetValue(banditKey, out BanditEvaluator.BanditModel? model)
                ? model
                : null;
        }
    }

    public void UpdateModel(string banditKey, BanditEvaluator.BanditModel model)
    {
        lock (_lock) _models[banditKey] = model;
    }
}
