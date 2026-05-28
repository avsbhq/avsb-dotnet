namespace Avsb.SDK;

/// <summary>Per-evaluation log entry. Mirrors §2.5 DecisionLogEntry.</summary>
public sealed record DecisionLogEntry
{
    public string FlagKey { get; init; } = string.Empty;

    /// <summary>
    /// Redacted to "[REDACTED]" when a private attribute contributed to the decision.
    /// </summary>
    public object? Value { get; init; }

    public string? VariationKey { get; init; }
    public EvaluationSource Source { get; init; }
    public string? RuleId { get; init; }
    public RuleType? RuleType { get; init; }

    /// <summary>
    /// Reasons with private attribute values replaced by "[REDACTED]".
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>UTC ms epoch when evaluated.</summary>
    public long EvaluatedAt { get; init; }

    /// <summary>Microseconds elapsed in the evaluator.</summary>
    public long DurationMicros { get; init; }

    /// <summary>Kind + key pairs — attributes intentionally omitted to avoid PII.</summary>
    public IReadOnlyList<ContextSummaryEntry> ContextSummary { get; init; }
        = Array.Empty<ContextSummaryEntry>();

    /// <summary>True when this entry was fired manually rather than auto-triggered.</summary>
    public bool ManuallyFired { get; init; }
}

/// <summary>Kind + key summary of one sub-context. Attributes are never logged.</summary>
public sealed record ContextSummaryEntry(string Kind, string Key);

/// <summary>Accumulated decision log for an SDK instance. Mirrors §2.5 DecisionLog.</summary>
public sealed class DecisionLog
{
    private readonly List<DecisionLogEntry> _entries = new();
    private readonly object _lock = new();

    public IReadOnlyList<DecisionLogEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToArray();
        }
    }

    internal void Add(DecisionLogEntry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    /// <summary>Redact private attribute values in reason strings.</summary>
    public static DecisionLogEntry Redact(DecisionLogEntry entry, IReadOnlySet<string> privateAttributes)
    {
        if (privateAttributes.Count == 0) return entry;

        var redactedReasons = entry.Reasons
            .Select(r => RedactReason(r, privateAttributes))
            .ToArray();

        // Value redaction: if any private attribute key appears in the stringified value, redact.
        var valueStr = entry.Value?.ToString() ?? string.Empty;
        var valueRedacted = privateAttributes.Any(a => valueStr.Contains(a, StringComparison.OrdinalIgnoreCase))
            ? "[REDACTED]"
            : entry.Value;

        return entry with { Reasons = redactedReasons, Value = valueRedacted };
    }

    private static string RedactReason(string reason, IReadOnlySet<string> privateAttributes)
    {
        foreach (var attr in privateAttributes)
        {
            reason = reason.Replace(attr, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }
        return reason;
    }
}
