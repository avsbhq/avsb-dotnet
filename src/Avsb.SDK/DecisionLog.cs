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

    /// <summary>Kind + key pairs; attributes intentionally omitted to avoid PII.</summary>
    public IReadOnlyList<ContextSummaryEntry> ContextSummary { get; init; }
        = Array.Empty<ContextSummaryEntry>();

    /// <summary>True when this entry was fired manually rather than auto-triggered.</summary>
    public bool ManuallyFired { get; init; }
}

/// <summary>Kind + key summary of one sub-context. Attributes are never logged.</summary>
public sealed record ContextSummaryEntry(string Kind, string Key);

/// <summary>
/// Bounded, append-only decision log for one client. Mirrors DecisionLog in types.ts.
///
/// BOUNDED is the important word. An unbounded collector grows for the life of the process unless
/// the caller discovers a flush method, which is a memory leak installed by default in a
/// long-running server. This keeps the most recent <see cref="Capacity"/> entries and drops the
/// oldest, so a service that never reads the log pays a fixed cost.
/// </summary>
public sealed class DecisionLog
{
    /// <summary>Entries retained when the caller does not choose.</summary>
    public const int DefaultCapacity = 1_000;

    private readonly Queue<DecisionLogEntry> _entries;
    private readonly object _lock = new();

    /// <param name="capacity">
    /// How many entries to retain. Zero disables recording entirely, which is what a caller who
    /// never reads the log should pick.
    /// </param>
    public DecisionLog(int capacity = DefaultCapacity)
    {
        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "DecisionLog capacity must not be negative.");
        }
        Capacity = capacity;
        _entries = new Queue<DecisionLogEntry>();
    }

    /// <summary>How many entries this log retains before dropping the oldest.</summary>
    public int Capacity { get; }

    /// <summary>All retained entries in append order. Immutable snapshot.</summary>
    public IReadOnlyList<DecisionLogEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToArray();
        }
    }

    internal void Add(DecisionLogEntry entry)
    {
        lock (_lock)
        {
            if (Capacity == 0) return;
            while (_entries.Count >= Capacity) _entries.Dequeue();
            _entries.Enqueue(entry);
        }
    }

    /// <summary>Drop every retained entry.</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    /// <summary>
    /// Take every retained entry and clear the log, for callers that ship the audit trail
    /// somewhere on a timer.
    /// </summary>
    public IReadOnlyList<DecisionLogEntry> Drain()
    {
        lock (_lock)
        {
            DecisionLogEntry[] snapshot = _entries.ToArray();
            _entries.Clear();
            return snapshot;
        }
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
