extern alias BCCrypto2;
using System.Text;
using AZOA.WebAPI.Services.NodeAdmin;
using FluentAssertions;
using Ed25519PrivateKeyParameters = BCCrypto2::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters;
using Ed25519Signer = BCCrypto2::Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace AZOA.WebAPI.Tests.NodeAdmin;

/// <summary>Plan §8 H2/H4 golden vectors: the CLI (lane B) reproduces the same bytes, so drift here breaks every login.</summary>
public sealed class NodeAdminWireContractTests
{
    private const string Seed = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string PublicKey = "11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo";
    private const string Audience = "https://node.example.test";

    public static TheoryData<string, string, string> GoldenSignatures => new()
    {
        {
            "root_login",
            NodeAdminCrypto.LoginMessage("root_login", Audience, "0b9e2f5a-6f0c-4e57-9a51-2b1f0e0c3d41",
                "q83vEjRWeJCrze8SNFZ4kKvN7xI0VniQq83vEjRWeJA", "2026-10-18T17:00:00.0000000+00:00"),
            "-NPZcjbxjAdPdW_KYSK6scxyFybe8EXi6VWz2clrUYPj43I-7dJJiYQTP8HFfpgixA1ChD2ymBji1Kl9ylA-Bw"
        },
        {
            "admin_login",
            NodeAdminCrypto.LoginMessage("admin_login", Audience, "7d4c1b2a-0e9f-4a8b-8c7d-6e5f4a3b2c1d",
                "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", "2026-10-18T17:00:00.0000000+00:00"),
            "daj9AS74SQyY-krCU38k00lgAcQXJMMtfx8u5DaXnRzGupenpanbZXfLiYPBkApyK9yAo9IjF-6OXNXzH0mFAQ"
        },
        {
            "enroll",
            NodeAdminCrypto.EnrollMessage(Audience, "3f2e1d0c-9b8a-4776-8554-433221100fed"),
            "8iizKreiGkkhhsPN7BpwI2IOHPQercUI2Je_Xg6BGTV4uWbMnMpQ_LaYcIYKmSOSa_VGbQgHsdembgsGQkSHAA"
        },
    };

    [Fact]
    public void Golden_key_has_the_contract_public_key_and_fingerprint()
    {
        var key = new Ed25519PrivateKeyParameters(Convert.FromHexString(Seed), 0);
        var publicKey = key.GeneratePublicKey().GetEncoded();

        NodeAdminCrypto.Base64Url(publicKey).Should().Be(PublicKey);
        NodeAdminCrypto.Fingerprint(publicKey).Should().Be("21fe31dfa154a261");
    }

    [Theory]
    [MemberData(nameof(GoldenSignatures))]
    public void Canonical_messages_reproduce_the_golden_signatures_and_verify(string purpose, string message, string expectedSignature)
    {
        message.Should().StartWith($"azoa-node-admin/v1\n{purpose}\n{Audience}\n").And.NotEndWith("\n");
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Convert.FromHexString(Seed), 0));
        var bytes = Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(bytes, 0, bytes.Length);

        NodeAdminCrypto.Base64Url(signer.GenerateSignature()).Should().Be(expectedSignature);
        NodeAdminCrypto.TryDecodePublicKey(PublicKey, out var publicKey).Should().BeTrue();
        NodeAdminCrypto.TryDecodeSignature(expectedSignature, out var signature).Should().BeTrue();
        NodeAdminCrypto.Verify(publicKey, message, signature).Should().BeTrue();
        NodeAdminCrypto.Verify(publicKey, message + " ", signature).Should().BeFalse("one changed byte of the signed string must fail");
        NodeAdminCrypto.Verify(publicKey, message.Replace(Audience, "https://evil.example.test"), signature)
            .Should().BeFalse("the audience binds the signature to one node");
    }

    [Theory]
    [InlineData("11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo=", false)]
    [InlineData("11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURp", false)]
    [InlineData("11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo", false)]
    [InlineData("11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHUR", false)]
    [InlineData("11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo", true)]
    public void Public_keys_decode_only_from_canonical_unpadded_base64url_of_32_bytes(string encoded, bool valid)
        => NodeAdminCrypto.TryDecodePublicKey(encoded, out _).Should().Be(valid);

    [Fact]
    public void Signed_timestamps_use_seven_fraction_digits_and_an_explicit_utc_offset()
        => NodeAdminCrypto.FormatTimestamp(new DateTimeOffset(2026, 10, 18, 19, 0, 0, TimeSpan.FromHours(2)))
            .Should().Be("2026-10-18T17:00:00.0000000+00:00");

    [Fact]
    public void Audit_hash_follows_the_canonical_formula_and_chains()
    {
        var first = NodeAdminAuditLog.ComputeHash(NodeAdminAuditLog.GenesisHash, 1, "2026-10-18T17:00:00.0000000+00:00",
            "root:21fe31dfa154a261", "POST /api/node-admin/admins", "", "intent", "");
        var second = NodeAdminAuditLog.ComputeHash(first, 2, "2026-10-18T17:00:01.5000000+00:00",
            "admin:3f2e1d0c-9b8a-4776-8554-433221100fed", "auth.session", "fp=21fe31dfa154a261", "failed",
            "{\"reason\":\"bad_or_replayed_totp\"}");

        // Independent vectors computed with Python hashlib over the documented canonical string.
        first.Should().Be("37cd698c53ea28290ae2ed54a22443a1f640fae744522f3ba11edb41808ddf92");
        second.Should().Be("dc7cf3a7181015eec19c4f11093fb06105fd27a6fc084024433e4a1a196a1775");
    }

    [Fact]
    public void Stored_session_ids_and_handles_never_reveal_the_token_sid()
    {
        var (sid, recordId, handle) = NodeAdminCrypto.NewSession();

        NodeAdminCrypto.TrySessionRecordId(sid, out var parsed).Should().BeTrue();
        parsed.Should().Be(recordId, "the validator finds the row by hashing the token's sid");
        recordId.Should().HaveLength(64).And.NotContain(sid);
        NodeAdminCrypto.IsSessionHandle(handle).Should().BeTrue();
        NodeAdminCrypto.TrySessionRecordId(handle, out var fromHandle).Should().BeFalse("a listed handle used as a sid must not resolve");
        fromHandle.Should().BeEmpty();
        NodeAdminCrypto.TrySessionRecordId(recordId, out _).Should().BeFalse("a leaked record id is not a usable sid");
    }
}
