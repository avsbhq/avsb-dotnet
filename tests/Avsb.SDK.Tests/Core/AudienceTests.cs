using System.Text.Json;
using Avsb.SDK.Core;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

public sealed class AudienceTests
{
    private static FlagDatafileAudience BuildAudience(string attribute, string op, string value,
        string? contextKind = null)
    {
        // Serialised, not interpolated: a value such as the regex "^[a-z]+@example\.com$" must be
        // JSON-escaped, and splicing it into a string literal produced invalid JSON.
        var condition = new Dictionary<string, object?>
        {
            ["id"] = "c",
            ["type"] = "CUSTOM_ATTRIBUTE",
            ["attribute"] = attribute,
            ["operator"] = op,
            ["value"] = value,
        };
        if (contextKind is not null) condition["contextKind"] = contextKind;
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "a",
            ["rules"] = new Dictionary<string, object?>
            {
                ["id"] = "g",
                ["logic"] = "AND",
                ["conditions"] = new[] { condition },
            },
        });
        return JsonSerializer.Deserialize<FlagDatafileAudience>(json, JsonOptions.Default)!;
    }

    [Fact]
    public void Equals_Operator_Matches_WhenAttributeMatches()
    {
        var audience = BuildAudience("plan", "equals", "paid");
        var ctx = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "paid" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void Equals_Operator_DoesNotMatch_WhenAttributeDiffers()
    {
        var audience = BuildAudience("plan", "equals", "paid");
        var ctx = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "free" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeFalse();
    }

    [Fact]
    public void NotEquals_Operator_Matches_WhenDifferent()
    {
        var audience = BuildAudience("plan", "not_equals", "paid");
        var ctx = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "free" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void Contains_Operator_Matches_CaseInsensitive()
    {
        var audience = BuildAudience("email", "contains", "example.com");
        var ctx = EvalContext.User("x", attributes: new Dictionary<string, object?> { ["email"] = "user@EXAMPLE.COM" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void NotContains_Operator_Matches_WhenAbsent()
    {
        var audience = BuildAudience("email", "not_contains", "example.com");
        var ctx = EvalContext.User("x", attributes: new Dictionary<string, object?> { ["email"] = "user@other.io" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void Exists_Operator_ReturnsFalse_WhenAttributeMissing()
    {
        var audience = BuildAudience("plan", "exists", "");
        var ctx = EvalContext.User("alice");
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeFalse();
    }

    [Fact]
    public void Exists_Operator_ReturnsTrue_WhenAttributePresent()
    {
        var audience = BuildAudience("plan", "exists", "");
        var ctx = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "paid" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void GreaterThan_Operator_Matches()
    {
        var audience = BuildAudience("age", "greater_than", "18");
        var ctx = EvalContext.User("x", attributes: new Dictionary<string, object?> { ["age"] = "25" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void LessThan_Operator_DoesNotMatch_WhenHigher()
    {
        var audience = BuildAudience("age", "less_than", "18");
        var ctx = EvalContext.User("x", attributes: new Dictionary<string, object?> { ["age"] = "25" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeFalse();
    }

    [Fact]
    public void ContextKind_Condition_ReturnsTrue_WhenSubContextMatches()
    {
        var audience = BuildAudience("tier", "equals", "enterprise", contextKind: "organization");
        var ctx = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["user"] = new SingleContext { Kind = "user", Key = "alice" },
                ["organization"] = new SingleContext { Kind = "organization", Key = "acme",
                    Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" } },
            },
        });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }

    [Fact]
    public void ContextKind_Condition_ReturnsFalse_WhenSubContextMissing()
    {
        var audience = BuildAudience("tier", "equals", "enterprise", contextKind: "organization");
        var ctx = EvalContext.User("alice"); // no organization sub-context
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeFalse();
    }

    [Fact]
    public void Regex_Operator_Matches()
    {
        var audience = BuildAudience("email", "regex", "^[a-z]+@example\\.com$");
        var ctx = EvalContext.User("x", attributes: new Dictionary<string, object?> { ["email"] = "test@example.com" });
        AudienceEvaluator.EvaluateAudience(audience, ctx).Should().BeTrue();
    }
}
