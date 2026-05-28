namespace Avsb.SDK.Core;

/// <summary>
/// Context helpers — kinded context lookup, bucketing key resolution,
/// and context summary. Mirrors context.ts.
/// </summary>
internal static class ContextHelper
{
    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.Ordinal) { "kind", "key", "name", "_meta" };

    /// <summary>
    /// Returns the single context for the given kind, or null when absent.
    /// For a SingleContext, returns itself when its kind matches.
    /// </summary>
    public static SingleContext? GetContextByKind(EvalContext ctx, string kind)
    {
        if (ctx.IsMulti)
        {
            ctx.Multi!.Contexts.TryGetValue(kind, out var sub);
            return sub;
        }
        return ctx.Single!.Kind == kind ? ctx.Single : null;
    }

    /// <summary>
    /// The canonical bucketing key for a kind; empty string when absent.
    /// </summary>
    public static string BucketingKey(EvalContext ctx, string kind = "user")
    {
        var sub = GetContextByKind(ctx, kind);
        return sub?.Key ?? string.Empty;
    }

    /// <summary>
    /// Produce kind+key pairs for the decision log. Attributes are omitted.
    /// </summary>
    public static IReadOnlyList<ContextSummaryEntry> ContextSummary(EvalContext ctx)
    {
        if (ctx.IsMulti)
        {
            return ctx.Multi!.Contexts.Values
                .Select(s => new ContextSummaryEntry(s.Kind, s.Key))
                .ToArray();
        }
        return new[] { new ContextSummaryEntry(ctx.Single!.Kind, ctx.Single.Key) };
    }

    /// <summary>
    /// Resolve a dotted attribute path against an EvalContext.
    /// 'user.key' → key of the user sub-context.
    /// 'organization.tier' → tier attribute of the organization sub-context.
    /// Non-dotted path → legacy fallback against primary context.
    /// Returns null when the path cannot be resolved.
    /// </summary>
    public static object? ResolveDottedAttribute(EvalContext ctx, string dottedPath)
    {
        if (string.IsNullOrEmpty(dottedPath)) return null;

        int dot = dottedPath.IndexOf('.');
        if (dot > 0)
        {
            var kind = dottedPath[..dot];
            var attr = dottedPath[(dot + 1)..];
            var sub = GetContextByKind(ctx, kind);
            if (sub is null) return null;
            return ReadSubContextAttribute(sub, attr);
        }

        // Non-dotted — legacy lookup: 'userId' → primary key, otherwise attribute.
        var primary = PrimaryContext(ctx);
        if (primary is null) return null;
        if (dottedPath == "userId") return primary.Key;
        return ReadSubContextAttribute(primary, dottedPath);
    }

    /// <summary>
    /// Read an attribute from a single context, honouring reserved aliases.
    /// </summary>
    public static object? ReadSubContextAttribute(SingleContext sub, string attribute)
    {
        if (attribute == "key") return sub.Key;
        if (attribute == "name") return sub.Name;
        if (ReservedKeys.Contains(attribute)) return null;
        sub.Attributes.TryGetValue(attribute, out var val);
        return val;
    }

    /// <summary>
    /// Aggregate private attributes from all sub-contexts.
    /// </summary>
    public static IReadOnlySet<string> PrivateAttributesFrom(EvalContext ctx)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ctx.IsMulti)
        {
            foreach (var sub in ctx.Multi!.Contexts.Values)
                Collect(sub, result);
        }
        else
        {
            Collect(ctx.Single!, result);
        }
        return result;
    }

    private static void Collect(SingleContext sub, HashSet<string> target)
    {
        if (sub.Meta?.PrivateAttributes is null) return;
        foreach (var k in sub.Meta.PrivateAttributes) target.Add(k);
    }

    private static SingleContext? PrimaryContext(EvalContext ctx)
    {
        if (!ctx.IsMulti) return ctx.Single;
        if (ctx.Multi!.Contexts.TryGetValue("user", out var user)) return user;
        return ctx.Multi.Contexts.Values.FirstOrDefault();
    }
}
