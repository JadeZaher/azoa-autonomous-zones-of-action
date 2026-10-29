using AZOA.WebAPI.Helpers;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Helpers;

/// <summary>What a stored claim-invite value must look like before a presented token can redeem it; see Helpers/AGENTS.md §opaque-token-digest.</summary>
public sealed class OpaqueTokenDigestTests
{
    private const string Token = "x7Kq2-member-claim-token";

    public static TheoryData<string, string?, string?, bool> Comparisons()
    {
        var digest = OpaqueTokenDigest.Compute(Token);
        return new()
        {
            { "the digest of the presented token", digest, Token, true },
            { "a legacy row that stored the raw token", Token, Token, false },
            { "the stored digest presented as if it were the token", digest, digest, false },
            { "another token's digest", OpaqueTokenDigest.Compute(Token + "x"), Token, false },
            { "an uppercase-hex digest (not the canonical v1 form)", "h1:" + digest[3..].ToUpperInvariant(), Token, false },
            { "a bare hex digest without the version prefix", digest[3..], Token, false },
            { "a truncated digest", digest[..^1], Token, false },
            { "no stored value", null, Token, false },
            { "no presented token", digest, "", false },
        };
    }

    [Theory]
    [MemberData(nameof(Comparisons))]
    public void Only_the_v1_digest_of_the_presented_token_matches(string because, string? stored, string? presented, bool matches)
        => OpaqueTokenDigest.Matches(stored, presented).Should().Be(matches, because);

    [Fact]
    public void The_v1_form_is_the_prefixed_lowercase_sha256_hex()
        => OpaqueTokenDigest.Compute("abc").Should()
            .Be("h1:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA-256('abc') is the FIPS 180-2 test vector");
}
