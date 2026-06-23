using FluentAssertions;
using AZOA.WebAPI.Core.Blockchain;
using System.Text;

namespace AZOA.WebAPI.Tests.Core;

/// <summary>
/// Offline correctness tests for the Tinyman V2 pool-address derivation. The
/// SHA-512/256 known-answer vectors and the canonical Algorand zero-address
/// prove the hand-rolled crypto/encoding is exact, independent of any network.
/// </summary>
public class TinymanV2PoolLocatorTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Sha512_256_EmptyString_MatchesNistVector()
    {
        Hex(TinymanV2PoolLocator.Sha512_256(Array.Empty<byte>()))
            .Should().Be("c672b8d1ef56ed28ab87c3622c5114069bdd3ad7b8f9737498d0c01ecef0967a");
    }

    [Fact]
    public void Sha512_256_Abc_MatchesNistVector()
    {
        Hex(TinymanV2PoolLocator.Sha512_256(Encoding.ASCII.GetBytes("abc")))
            .Should().Be("53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23");
    }

    [Fact]
    public void EncodeAlgorandAddress_ZeroPublicKey_IsCanonicalZeroAddress()
    {
        // The well-known Algorand "zero address" — validates checksum + base32.
        TinymanV2PoolLocator.EncodeAlgorandAddress(new byte[32])
            .Should().Be("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ");
    }

    [Fact]
    public void GetPoolAddress_IsDeterministicAndOrderIndependent()
    {
        // Tinyman sorts the pair internally, so (a,b) and (b,a) must match.
        var ab = TinymanV2PoolLocator.GetPoolAddress(
            TinymanV2PoolLocator.TestnetValidatorAppId, 0, 10458941);
        var ba = TinymanV2PoolLocator.GetPoolAddress(
            TinymanV2PoolLocator.TestnetValidatorAppId, 10458941, 0);

        ab.Should().Be(ba);
        ab.Should().HaveLength(58);
        ab.Should().MatchRegex("^[A-Z2-7]+$");
    }
}
