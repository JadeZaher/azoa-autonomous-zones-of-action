using System.Text;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>RFC 6238 Appendix B SHA1 vectors, the ±1 step window and the replay guard.</summary>
public sealed class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Codes_match_the_rfc_6238_sha1_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Totp.Code(RfcSecret, step, digits: 8).Should().Be(expected);
        Totp.Code(RfcSecret, step).Should().Be(expected[^6..], "six-digit codes are the low-order digits of the same HOTP value");
    }

    [Theory]
    [InlineData(-1, 0L, true)]
    [InlineData(0, 0L, true)]
    [InlineData(1, 0L, true)]
    [InlineData(-2, 0L, false)]
    [InlineData(2, 0L, false)]
    public void Verification_accepts_one_step_of_skew_either_way(int offset, long lastUsedStepDelta, bool accepted)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var current = Totp.StepAt(now);
        var code = Totp.Code(RfcSecret, current + offset);

        Totp.TryVerify(RfcSecret, code, now, lastUsedStepDelta, out var matched).Should().Be(accepted);
        if (accepted) matched.Should().Be(current + offset);
    }

    [Fact]
    public void A_code_for_an_already_used_step_is_refused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var current = Totp.StepAt(now);
        var code = Totp.Code(RfcSecret, current);

        Totp.TryVerify(RfcSecret, code, now, current - 1, out _).Should().BeTrue();
        Totp.TryVerify(RfcSecret, code, now, current, out _).Should().BeFalse("the replay guard requires step > totp_last_step");
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Malformed_codes_are_refused(string code)
        => Totp.TryVerify(RfcSecret, code, DateTimeOffset.UtcNow, 0, out _).Should().BeFalse();

    [Fact]
    public void Secrets_encode_as_unpadded_rfc_4648_base32_in_the_contract_otpauth_uri()
    {
        var base32 = Totp.ToBase32(RfcSecret);

        base32.Should().Be("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Totp.ProvisioningUri("node.example.test", "alice", base32).Should().Be(
            "otpauth://totp/AZOA%20Node%20(node.example.test):alice?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
            + "&issuer=AZOA%20Node%20(node.example.test)&algorithm=SHA1&digits=6&period=30");
    }
}
