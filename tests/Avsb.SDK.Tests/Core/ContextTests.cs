using Avsb.SDK.Core;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

public sealed class ContextTests
{
    [Fact]
    public void GetContextByKind_SingleContext_ReturnsSelf_WhenKindMatches()
    {
        var ctx = EvalContext.User("alice");
        var result = ContextHelper.GetContextByKind(ctx, "user");
        result.Should().NotBeNull();
        result!.Key.Should().Be("alice");
    }

    [Fact]
    public void GetContextByKind_SingleContext_ReturnsNull_WhenKindMismatches()
    {
        var ctx = EvalContext.User("alice");
        var result = ContextHelper.GetContextByKind(ctx, "organization");
        result.Should().BeNull();
    }

    [Fact]
    public void GetContextByKind_MultiContext_ReturnsCorrectSubContext()
    {
        var multi = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["user"] = new SingleContext { Kind = "user", Key = "alice" },
                ["organization"] = new SingleContext { Kind = "organization", Key = "acme",
                    Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" } },
            },
        });

        var org = ContextHelper.GetContextByKind(multi, "organization");
        org.Should().NotBeNull();
        org!.Key.Should().Be("acme");
    }

    [Fact]
    public void ResolveDottedAttribute_DottedPath_ResolvesSubContextAttribute()
    {
        var ctx = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["organization"] = new SingleContext { Kind = "organization", Key = "acme",
                    Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" } },
            },
        });

        var result = ContextHelper.ResolveDottedAttribute(ctx, "organization.tier");
        result.Should().Be("enterprise");
    }

    [Fact]
    public void ResolveDottedAttribute_UserKey_ReturnsKey()
    {
        var ctx = EvalContext.User("alice");
        var result = ContextHelper.ResolveDottedAttribute(ctx, "user.key");
        result.Should().Be("alice");
    }

    [Fact]
    public void BucketingKey_ReturnsUserKey_ForSingleContext()
    {
        var ctx = EvalContext.User("bob");
        ContextHelper.BucketingKey(ctx).Should().Be("bob");
    }

    [Fact]
    public void ContextSummary_MultiContext_ReturnsAllKinds()
    {
        var ctx = EvalContext.FromMulti(new MultiContext
        {
            Contexts = new Dictionary<string, SingleContext>
            {
                ["user"] = new SingleContext { Kind = "user", Key = "alice" },
                ["organization"] = new SingleContext { Kind = "organization", Key = "acme" },
            },
        });

        var summary = ContextHelper.ContextSummary(ctx);
        summary.Should().HaveCount(2);
        summary.Should().Contain(e => e.Kind == "user" && e.Key == "alice");
        summary.Should().Contain(e => e.Kind == "organization" && e.Key == "acme");
    }
}
