using FluentAssertions;

namespace Avsb.SDK.Tests;

public sealed class DecisionLogTests
{
    [Fact]
    public void Redact_DoesNotModify_WhenNoPrivateAttributes()
    {
        var entry = BuildEntry("user-plan", "paid");
        var result = DecisionLog.Redact(entry, new HashSet<string>());
        result.Reasons.Should().BeEquivalentTo(entry.Reasons);
        result.Value.Should().Be(entry.Value);
    }

    [Fact]
    public void Redact_ReplacesPrivateAttributeValue_InReasons()
    {
        var entry = BuildEntry("plan", "paid", reasons: new[] { "attribute plan=paid matched" });
        var result = DecisionLog.Redact(entry, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "plan" });
        result.Reasons[0].Should().Contain("[REDACTED]");
        result.Reasons[0].Should().NotContain("plan");
    }

    [Fact]
    public void Redact_DoesNotAlter_UnrelatedReasons()
    {
        var entry = BuildEntry("plan", "paid", reasons: new[] { "no match, returning default" });
        var result = DecisionLog.Redact(entry, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "secret" });
        result.Reasons[0].Should().Be("no match, returning default");
    }

    [Fact]
    public void DecisionLog_Add_IsVisibleInEntries()
    {
        var log = new DecisionLog();
        var entry = BuildEntry("feature-x", true);
        log.Add(entry);
        log.Entries.Should().HaveCount(1);
        log.Entries[0].FlagKey.Should().Be("feature-x");
    }

    [Fact]
    public void DecisionLog_ThreadSafe_MultipleAdds()
    {
        var log = new DecisionLog();
        Parallel.For(0, 50, i =>
        {
            log.Add(BuildEntry($"flag-{i}", i));
        });
        log.Entries.Should().HaveCount(50);
    }

    private static DecisionLogEntry BuildEntry(string flagKey, object? value,
        string[]? reasons = null) =>
        new DecisionLogEntry
        {
            FlagKey = flagKey,
            Value = value,
            Source = EvaluationSource.Rule,
            RuleId = "r1",
            RuleType = RuleType.AbTest,
            Reasons = reasons ?? new[] { $"flag {flagKey} evaluated" },
            EvaluatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
}
