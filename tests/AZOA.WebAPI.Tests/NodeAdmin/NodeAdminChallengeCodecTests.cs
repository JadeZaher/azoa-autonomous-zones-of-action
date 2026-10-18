using System.Net;
using AZOA.WebAPI.Core.Networking;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>NEW-1: stateless challenge ids authenticate their exact strings; the operator-login limiter buckets IPv6 by /64.</summary>
public sealed class NodeAdminChallengeCodecTests
{
    private static readonly NodeAdminChallengePayload Payload = new(1, "0123456789abcdef0123456789abcdef", "root_login",
        "21fe31dfa154a261", "https://node.example.test", "q83vEjRWeJCrze8SNFZ4kKvN7xI0VniQq83vEjRWeJA", "2026-10-18T17:00:00.0000000+00:00");

    [Fact]
    public void A_minted_challenge_round_trips_its_exact_strings()
    {
        var codec = Codec(jwtKey: "jwt-key-one-0123456789012345678901234567");

        codec.TryDecode(codec.Encode(Payload), out var decoded).Should().BeTrue();
        decoded.Should().Be(Payload);
    }

    [Fact]
    public void Any_changed_byte_a_different_key_or_garbage_is_refused()
    {
        var codec = Codec(jwtKey: "jwt-key-one-0123456789012345678901234567");
        var encoded = codec.Encode(Payload);
        var flipped = (encoded[10] == 'A' ? 'B' : 'A') + "";

        codec.TryDecode(encoded[..10] + flipped + encoded[11..], out _).Should().BeFalse();
        Codec(jwtKey: "jwt-key-two-0123456789012345678901234567").TryDecode(encoded, out _).Should().BeFalse();
        Codec(jwtKey: "jwt-key-one-0123456789012345678901234567", challengeKey: "dedicated-challenge-secret").TryDecode(encoded, out _)
            .Should().BeFalse("a dedicated ChallengeKey replaces the Jwt:Key derivation");
        codec.TryDecode(Guid.NewGuid().ToString("D"), out _).Should().BeFalse();
        codec.TryDecode(encoded + "A", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void Operator_login_buckets_ipv4_by_address_and_ipv6_by_slash_64(string address, string expected)
        => RateLimitPartitionKey.Address(IPAddress.Parse(address)).Should().Be(expected);

    private static NodeAdminChallengeCodec Codec(string jwtKey, string? challengeKey = null)
        => new(Options.Create(new NodeAdminOptions { ChallengeKey = challengeKey }),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = jwtKey }).Build());
}
