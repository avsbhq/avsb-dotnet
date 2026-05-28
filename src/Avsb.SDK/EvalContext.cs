namespace Avsb.SDK;

/// <summary>
/// Metadata attached to a context (private attribute redaction, anonymous flag).
/// Mirrors ContextMeta (§2.1).
/// </summary>
public sealed record ContextMeta
{
    public IReadOnlyList<string>? PrivateAttributes { get; init; }
    public bool Anonymous { get; init; }
}

/// <summary>
/// A single kinded identity (e.g. kind="user", key="alice"). Mirrors SingleContext (§2.1).
/// </summary>
public sealed record SingleContext
{
    public string Kind { get; init; } = "user";
    public string Key { get; init; } = string.Empty;
    public string? Name { get; init; }
    public ContextMeta? Meta { get; init; }

    /// <summary>Extra targeting attributes (not kind/key/name/meta).</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; }
        = new Dictionary<string, object?>();
}

/// <summary>
/// A multi-context container keyed by context kind. Mirrors MultiContext (§2.1).
/// </summary>
public sealed record MultiContext
{
    public IReadOnlyDictionary<string, SingleContext> Contexts { get; init; }
        = new Dictionary<string, SingleContext>();
}

/// <summary>
/// Discriminated union for evaluation context. Exactly one side is non-null.
/// </summary>
public sealed record EvalContext
{
    private EvalContext() { }

    public SingleContext? Single { get; private init; }
    public MultiContext? Multi { get; private init; }
    public bool IsMulti => Multi is not null;

    /// <summary>Build a single-context EvalContext.</summary>
    public static EvalContext FromSingle(SingleContext ctx) =>
        new() { Single = ctx };

    /// <summary>Build a multi-context EvalContext.</summary>
    public static EvalContext FromMulti(MultiContext ctx) =>
        new() { Multi = ctx };

    /// <summary>
    /// Convenience factory: a plain user context with optional extra attributes.
    /// </summary>
    public static EvalContext User(string key, string? name = null,
        IDictionary<string, object?>? attributes = null,
        ContextMeta? meta = null) =>
        FromSingle(new SingleContext
        {
            Kind = "user",
            Key = key,
            Name = name,
            Meta = meta,
            Attributes = (attributes is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(attributes)),
        });
}
