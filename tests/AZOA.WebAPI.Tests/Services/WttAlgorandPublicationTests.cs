using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Wormhole;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttAlgorandPublicationTests
{
    [Theory]
    [InlineData("Transfer")]
    [InlineData("Attest")]
    public void Exact_core_inner_call_preserves_full_sequence_and_attested_normalization(string stage)
    {
        var intent = Intent();
        var publication = WttAlgorandPublication.Read(Receipt(intent, stage), intent, stage, "source-call", "block:500", 1234);
        Assert.NotNull(publication);
        Assert.Equal(ulong.MaxValue.ToString(), publication.Sequence);
        Assert.Equal("1", publication.EventIndex);
        Assert.Equal(0u, publication.Nonce);
        Assert.Equal((byte)0, publication.ConsistencyLevel);
        var body = Convert.FromHexString(publication.BodyHex);
        Assert.Equal(1234u, BinaryPrimitives.ReadUInt32BigEndian(body));
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(42)));
        Assert.Equal(publication.PayloadHex, Convert.ToHexStringLower(body.AsSpan(51)));
        if (stage == "Attest") Assert.Equal((byte)8, Convert.FromHexString(publication.PayloadHex)[35]);
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("nonce")]
    [InlineData("payload")]
    [InlineData("sequence")]
    [InlineData("duplicate")]
    [InlineData("core")]
    [InlineData("outer")]
    public void Other_body_authority_or_multiple_publications_are_not_confirmations(string mutation)
    {
        var intent = Intent();
        Assert.Null(WttAlgorandPublication.Read(Receipt(intent, "Transfer", mutation), intent, "Transfer", "source-call", "block:500", 1234));
    }

    private static JsonElement Receipt(WttOperationIntent intent, string stage, string mutation = "")
    {
        var payload = stage == "Attest"
            ? WttPayloadCodec.EncodeAttestation(new(intent.Asset.OriginAddress, 8, 8, intent.Asset.SymbolHex, intent.Asset.NameHex))
            : WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, 8,
                intent.RecipientProtocolAddress, 2, intent.Amount.Fee));
        if (mutation == "payload") payload[^1] ^= 1;
        var nonce = new byte[8]; if (mutation == "nonce") nonce[7] = 1;
        var sequence = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(sequence, ulong.MaxValue);
        var publication = new Dictionary<string, object>
        {
            ["tx-type"] = "appl", ["sender"] = global::Algorand.Address.ForApplication(mutation == "sender" ? 10UL : 9UL).ToString(),
            ["application-transaction"] = new Dictionary<string, object>
            {
                ["application-id"] = mutation == "core" ? 6 : 7, ["on-completion"] = "noop",
                ["application-args"] = new[] { Convert.ToBase64String("publishMessage"u8), Convert.ToBase64String(payload), Convert.ToBase64String(nonce) }
            },
            ["logs"] = new[] { Convert.ToBase64String(mutation == "sequence" ? sequence[..7] : sequence) }
        };
        var children = new List<object> { new { }, publication };
        if (mutation == "duplicate") children.Add(publication);
        return JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["id"] = mutation == "outer" ? "another-call" : "source-call",
            ["application-transaction"] = new Dictionary<string, object> { ["application-id"] = 9 }, ["inner-txns"] = children
        });
    }

    private static WttOperationIntent Intent()
    {
        var context = SigningContext.ForUser(Guid.NewGuid(), Guid.NewGuid()).ForChain("Algorand");
        var network = new WttNetworkDescriptor("Algorand", ChainNetwork.Devnet, "algorand:fixture", "Devnet", 8, "7", "9", "pins",
            new(8, new string('1', 64), new string('2', 64), new string('3', 64)),
            Convert.ToHexStringLower(global::Algorand.Address.ForApplication(9).Bytes));
        return new(1, "publication", context.UserId, null, network, network with { ProtocolChain = 2 },
            new("123", false, 19, 8, new string('a', 64), 8, new string('0', 64), new string('0', 64), "behavior"),
            new("owner", context), new("payer", context), "recipient", new string('b', 64), null, 8,
            new("1000000000000", "1000000000000", "0", "100", "1", "99"), 0, 0, false, true,
            new("10000", "10000", "10000"), new("10000", "10000", "10000"));
    }
}
