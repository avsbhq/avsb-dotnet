using Avsb.SDK.Sticky;
using FluentAssertions;

namespace Avsb.SDK.Tests;

public sealed class StickyTests
{
    [Fact]
    public void Lookup_ReturnsNull_WhenNothingSaved()
    {
        var svc = new InMemoryStickyBucketService();
        svc.Lookup("alice", "flag-a").Should().BeNull();
    }

    [Fact]
    public void Save_ThenLookup_ReturnsSameAssignment()
    {
        var svc = new InMemoryStickyBucketService();
        var assignment = new StickyAssignment
        {
            VariationId = "var-treatment",
            RuleId = "rule-1",
            RuleType = RuleType.AbTest,
            AssignedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        svc.Save("alice", "flag-a", assignment);
        var result = svc.Lookup("alice", "flag-a");
        result.Should().NotBeNull();
        result!.VariationId.Should().Be("var-treatment");
        result.RuleId.Should().Be("rule-1");
    }

    [Fact]
    public void Save_OverwritesPreviousAssignment()
    {
        var svc = new InMemoryStickyBucketService();
        svc.Save("alice", "flag-a", new StickyAssignment
        {
            VariationId = "var-control", RuleId = "r1",
            RuleType = RuleType.AbTest, AssignedAt = 1000,
        });
        svc.Save("alice", "flag-a", new StickyAssignment
        {
            VariationId = "var-treatment", RuleId = "r1",
            RuleType = RuleType.AbTest, AssignedAt = 2000,
        });
        svc.Lookup("alice", "flag-a")!.VariationId.Should().Be("var-treatment");
    }

    [Fact]
    public void Lookup_IsKeyedByFlagAndUser()
    {
        var svc = new InMemoryStickyBucketService();
        svc.Save("alice", "flag-a", new StickyAssignment
        {
            VariationId = "v1", RuleId = "r1", RuleType = RuleType.AbTest, AssignedAt = 1,
        });
        // Different user — should not find alice's assignment.
        svc.Lookup("bob", "flag-a").Should().BeNull();
        // Different flag — should not find flag-a's assignment.
        svc.Lookup("alice", "flag-b").Should().BeNull();
    }
}
