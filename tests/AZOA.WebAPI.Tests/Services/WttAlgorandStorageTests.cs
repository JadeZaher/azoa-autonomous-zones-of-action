using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Wormhole;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandStorageTests
{
    [Fact]
    public void Source_storage_roles_cannot_reuse_foreign_or_dirty_blobs()
    {
        var bytes = new byte[1905];
        var empty = new WttAlgorandStorage.Blob(bytes, []);
        WttAlgorandStorage.IsSourceRole(empty, false).Should().BeTrue();
        WttAlgorandStorage.IsSourceRole(empty, true).Should().BeTrue();
        BinaryPrimitives.WriteUInt64BigEndian(bytes, 7);
        var emitter = new WttAlgorandStorage.Blob(bytes, "publishMessage"u8.ToArray());
        WttAlgorandStorage.IsSourceRole(emitter, true).Should().BeTrue();
        WttAlgorandStorage.IsSourceRole(emitter, false).Should().BeFalse();
        WttAlgorandStorage.IsSourceRole(empty, true).Should().BeFalse();
        bytes[8] = 1;
        WttAlgorandStorage.IsSourceRole(emitter, true).Should().BeFalse();
        WttAlgorandStorage.IsSourceRole(new(new byte[1905], "duplicate"u8.ToArray()), true).Should().BeFalse();
    }

    [Fact]
    public void Template_preserves_full_width_indices_and_application_ids()
    {
        var program = WttAlgorandStorage.Program(ulong.MaxValue, ulong.MaxValue, "guardian"u8);
        var expected = "0620010181ffffffffffffffffff01488008677561726469616e483110810612443119221244311881ffffffffffffffffff01"
            + "124431208020" + Convert.ToHexStringLower(global::Algorand.Address.ForApplication(ulong.MaxValue).Bytes)
            + "124431018100124431093203124431153203124422";
        Convert.ToHexStringLower(program).Should().Be(expected);
        var address = WttAlgorandStorage.Address(ulong.MaxValue, ulong.MaxValue, "guardian"u8);
        address.Should().Be(TinymanV2PoolLocator.EncodeAlgorandAddress(
            TinymanV2PoolLocator.Sha512_256([.. "Program"u8, .. program])));
        address.Should().NotBe(WttAlgorandStorage.Address(ulong.MaxValue - 1, ulong.MaxValue, "guardian"u8));
        address.Should().NotBe(WttAlgorandStorage.Address(ulong.MaxValue, ulong.MaxValue - 1, "guardian"u8));
    }

    [Fact]
    public void Zero_pages_keep_expiration_at_its_original_offset()
    {
        var (account, address) = Account();
        var blob = WttAlgorandStorage.Read(account, 7, address, 500)!;
        blob.Should().NotBeNull();
        blob.Bytes.Should().HaveCount(1905);
        var guardians = WttAlgorandStorage.ReadGuardians(blob)!;
        guardians.Addresses.Should().Equal(new string('1', 40), new string('2', 40));
        guardians.ExpiresAt.Should().Be(ulong.MaxValue - 1);
        blob.Bytes.AsSpan(127, 127).ToArray().Should().OnlyContain(value => value == 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("short")]
    [InlineData("integer")]
    [InlineData("foreign-authority")]
    [InlineData("wrong-round")]
    [InlineData("wrong-address")]
    public void Incomplete_or_unbound_account_cannot_supply_guardian_evidence(string mutation)
    {
        var (account, address) = Account(mutation);
        WttAlgorandStorage.Read(account, 7, address, 500).Should().BeNull();
    }

    [Fact]
    public void Wrapped_asset_binds_the_original_attestation_body_and_full_width_ASA()
    {
        var bytes = new byte[1905];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, ulong.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), 2);
        bytes.AsSpan(18, 32).Fill(0xbb);
        WttPayloadCodec.EncodeAttestation(new(new string('a', 64), 2, 18, new string('0', 64), new string('0', 64))).CopyTo(bytes, 59);
        var blob = new WttAlgorandStorage.Blob(bytes, "asset"u8.ToArray());
        var wrapped = WttAlgorandStorage.ReadWrappedAsset(blob)!;
        wrapped.AssetId.Should().Be(ulong.MaxValue);
        wrapped.Origin.Decimals.Should().Be(18);
        wrapped.Origin.TokenAddress.Should().Be(new string('a', 64));
        wrapped.EmitterAddress.Should().Be(new string('b', 64));
        bytes[159] = 1;
        WttAlgorandStorage.ReadWrappedAsset(blob).Should().BeNull();
        bytes[159] = 0; bytes[17] = 1;
        WttAlgorandStorage.ReadWrappedAsset(blob).Should().BeNull();
    }

    [Fact]
    public void Initialized_storage_without_a_role_is_not_guardian_evidence()
    {
        var (account, address) = Account("unassigned");
        var blob = WttAlgorandStorage.Read(account, 7, address, 500)!;
        blob.Should().NotBeNull();
        blob.Metadata.Should().BeEmpty();
        WttAlgorandStorage.ReadGuardians(blob).Should().BeNull();
    }

    [Fact]
    public void Guardian_role_and_unique_nonzero_keys_are_required()
    {
        var (account, address) = Account();
        var blob = WttAlgorandStorage.Read(account, 7, address, 500)!;
        WttAlgorandStorage.ReadGuardians(blob with { Metadata = "duplicate"u8.ToArray() }).Should().BeNull();
        blob.Bytes.AsSpan(1, 20).CopyTo(blob.Bytes.AsSpan(21, 20));
        WttAlgorandStorage.ReadGuardians(blob).Should().BeNull();
        blob.Bytes.AsSpan(21, 20).Clear();
        WttAlgorandStorage.ReadGuardians(blob).Should().BeNull();
    }

    private static (JsonElement Account, string Address) Account(string? mutation = null)
    {
        var address = WttAlgorandStorage.Address(7, 3, "guardian"u8);
        var bytes = new byte[1905];
        bytes[0] = 2;
        bytes.AsSpan(1, 20).Fill(0x11);
        bytes.AsSpan(21, 20).Fill(0x22);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1000), ulong.MaxValue - 1);
        var entries = new List<object>();
        for (byte page = 0; page < 15; page++)
        {
            if (mutation == "missing" && page == 3) continue;
            entries.Add(new { key = Convert.ToBase64String([mutation == "duplicate" && page == 3 ? (byte)2 : page]),
                value = new { type = mutation == "integer" && page == 3 ? 2 : 1,
                    bytes = Convert.ToBase64String(bytes.AsSpan(page * 127, mutation == "short" && page == 3 ? 126 : 127)), @uint = 0 } });
        }
        if (mutation != "unassigned") entries.Add(new { key = Convert.ToBase64String("meta"u8), value = new { type = 1,
            bytes = Convert.ToBase64String("guardian"u8), @uint = 0 } });
        return (JsonSerializer.SerializeToElement(new Dictionary<string, object> {
            ["address"] = mutation == "wrong-address" ? global::Algorand.Address.ForApplication(9).ToString() : address,
            ["auth-addr"] = global::Algorand.Address.ForApplication(mutation == "foreign-authority" ? 9UL : 7UL).ToString(),
            ["round"] = mutation == "wrong-round" ? 501 : 500,
            ["apps-local-state"] = new[] { new Dictionary<string, object> { ["id"] = 7, ["key-value"] = entries } }
        }), address);
    }
}
