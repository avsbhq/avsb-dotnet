using Avsb.SDK.Core;
using FluentAssertions;

namespace Avsb.SDK.Tests.Core;

public sealed class HashTests
{
    // Reference vectors computed from the canonical TypeScript implementation.

    [Fact]
    public void EmptyString_ReturnsDeterministicValue()
    {
        uint result = Hash.Murmurhash3(string.Empty);
        // MurmurHash3_x86_32("", 0) = 0
        result.Should().Be(0u);
    }

    [Theory]
    [InlineData("alice", 0u, 1280413405u)]
    [InlineData("bob", 0u, 2824567794u)]
    [InlineData("alice", 42u, 2290742874u)]
    public void KnownInputs_MatchTypeScriptOutputs(string key, uint seed, uint expected)
    {
        uint result = Hash.Murmurhash3(key, seed);
        result.Should().Be(expected, $"murmurhash3(\"{key}\", {seed}) should be {expected}");
    }

    [Fact]
    public void SameInput_AlwaysProducesSameOutput()
    {
        uint first = Hash.Murmurhash3("determinism-test");
        uint second = Hash.Murmurhash3("determinism-test");
        first.Should().Be(second);
    }

    [Fact]
    public void FourByteInput_ProcessesBodyPath()
    {
        // "test" is exactly 4 bytes — exercises the body loop rather than tail only.
        uint r1 = Hash.Murmurhash3("test");
        uint r2 = Hash.Murmurhash3("test");
        r1.Should().Be(r2);
        r1.Should().NotBe(0u); // not the trivial zero
    }

    [Fact]
    public void LongerKey_ProducesConsistentHash()
    {
        // "alicecheckout-v2-experiment" as used by the AB determinism conformance fixture.
        uint h1 = Hash.Murmurhash3("alicecheckout-v2-experiment") % 10_000;
        uint h2 = Hash.Murmurhash3("alicecheckout-v2-experiment") % 10_000;
        h1.Should().Be(h2);
        // The conformance fixture expects hash 4336 for this combination.
        h1.Should().Be(4336u);
    }
}
