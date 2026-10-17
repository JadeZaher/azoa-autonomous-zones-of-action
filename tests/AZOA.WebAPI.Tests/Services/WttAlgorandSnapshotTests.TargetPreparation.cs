using System.Buffers.Binary;
using System.Net;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttAlgorandSnapshotTests
{
    [Fact]
    public async Task Target_lost_signed_commit_and_send_recover_identical_wire_without_new_custody()
    {
        using var fixture = new AlgoPreparationFixture(target: true);
        var (intent, proofs, quote) = await fixture.TargetIntentAsync();
        fixture.LoseCommit = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Provider.PrepareWttTargetAsync(intent, proofs, quote, "TransferSequenceStorage", 0));
        fixture.AllowCustody = false;
        var prepared = await fixture.Provider.PrepareWttTargetAsync(intent, proofs, quote, "TransferSequenceStorage", 0);
        Assert.False(prepared.IsError, prepared.Message);
        Assert.Equal(1, fixture.CustodyCalls);
        var sent = new List<byte[]>();
        fixture.Rpc.Submit = async (request, ct) =>
        {
            sent.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            if (sent.Count == 1) throw new HttpRequestException("Lost possible target send.");
            return Json(new { txId = prepared.Result!.TransactionId });
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Provider.SubmitWttTargetAsync(intent, proofs, quote, prepared.Result!));
        await fixture.Provider.SubmitWttTargetAsync(intent, proofs, quote, prepared.Result!);
        Assert.Equal(sent[0], sent[1]);
        Assert.Equal(1, fixture.CustodyCalls);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider.SubmitWttTargetAsync(intent, proofs, quote,
            prepared.Result! with { TransactionId = "foreign" }));
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Target_redemption_requires_initialized_sequence_before_custody()
    {
        using var fixture = new AlgoPreparationFixture(target: true);
        var (intent, proofs, quote) = await fixture.TargetIntentAsync();
        var result = await fixture.Provider.PrepareWttTargetAsync(intent, proofs, quote, "Redeem", 0);
        Assert.True(result.IsError);
        Assert.Empty(fixture.Rows);
        Assert.Equal(0, fixture.CustodyCalls);
    }

    [Fact]
    public async Task External_recipient_signs_exact_durable_opt_in_without_payer_impersonation()
    {
        using var fixture = new AlgoPreparationFixture(target: true);
        var (intent, _, quote) = await fixture.TargetIntentAsync(externalRecipient: true);
        var refused = await fixture.Provider.PrepareWttTargetAsync(intent, WttQuotedProofs(quote), quote, "RecipientOptIn", 0);
        Assert.True(refused.IsError); Assert.Empty(fixture.Rows); Assert.Equal(0, fixture.CustodyCalls);
        var message = await fixture.Provider.PrepareWttParticipantAsync(intent, quote, "RecipientOptIn", 0);
        Assert.False(message.IsError, message.Message); Assert.Single(fixture.Rows);
        var original = message.Result!;
        var tx = Encoder.DecodeFromMsgPack<Transaction>(Convert.FromBase64String(original.UnsignedTransactionBase64));
        var wrong = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(tx.Sign(new global::Algorand.Algod.Model.Account())));
        Assert.True((await fixture.Provider.AcceptWttParticipantSignatureAsync(intent, quote, original, wrong)).IsError);
        Assert.Null(fixture.Rows.Values.Single().SignedPreparationJson);
        var signed = Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(tx.Sign(fixture.Participant)));
        var accepted = await fixture.Provider.AcceptWttParticipantSignatureAsync(intent, quote, original, signed);
        Assert.False(accepted.IsError, accepted.Message);
        Assert.Equal(accepted.Result, (await fixture.Provider.AcceptWttParticipantSignatureAsync(intent, quote, original, signed)).Result);
        Assert.True((await fixture.Provider.AcceptWttParticipantSignatureAsync(intent, quote,
            original with { ParticipantAddress = intent.TargetAuthority.Address }, signed)).IsError);
        Assert.Equal(0, fixture.CustodyCalls);
    }

    private static WttTargetProofs WttQuotedProofs(WttStageQuote quote)
    {
        var plan = System.Text.Json.JsonSerializer.Deserialize<WttAlgorandTargetPlan>(quote.PlanJson)!;
        return new(plan.Transfer, plan.Attestation);
    }

    private sealed partial class AlgoPreparationFixture
    {
        internal readonly global::Algorand.Algod.Model.Account Participant = new();
        private static void AddTargetVerifier(IServiceCollection registrations)
        {
            var crypto = new Mock<IWttGuardianSignatureVerifier>();
            crypto.Setup(value => value.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>())).Returns(true);
            registrations.AddSingleton(new WttProofVerifier(crypto.Object, TimeProvider.System,
                Options.Create(new WttFinalityOptions { Networks = [new("algorand:" + WttAlgorandSnapshotTests.Rpc.Genesis, 90)] })));
        }

        internal async Task<(WttOperationIntent, WttTargetProofs, WttStageQuote)> TargetIntentAsync(bool externalRecipient = false)
        {
            var owner = account.Address.ToString();
            var recipient = externalRecipient ? Participant.Address.ToString() : owner;
            HttpResponseMessage? RecipientAccount(HttpRequestMessage request) => externalRecipient && request.RequestUri!.AbsolutePath == "/v2/accounts/" + recipient
                ? Json(new Dictionary<string, object> { ["address"] = recipient, ["round"] = 500, ["amount"] = 1000000,
                    ["min-balance"] = 100000, ["assets"] = Array.Empty<object>() }) : null;
            rpc.ReadOverride = RecipientAccount;
            var asset = new WttAssetDescriptor("erc20", false, 18, 2, new string('c', 64), 18,
                new string('0', 64), new string('0', 64), "origin");
            var inspected = await Provider.InspectWttTargetAsync(asset, recipient, false);
            Assert.False(inspected.IsError, inspected.Message);
            var target = inspected.Result!.Network;
            var source = target with { ChainType = "Ethereum", ProtocolIdentity = "eip155:31337", ProtocolChain = 2, EmitterAddressHex = new string('b', 64) };
            var intent = new WttOperationIntent(1, "actual-target-preparation", context.UserId, null, source, target, asset,
                new(owner, context), new(owner, context), recipient, Convert.ToHexStringLower(new global::Algorand.Address(recipient).Bytes), ulong.MaxValue.ToString(), 8,
                WttPayloadCodec.Normalize("1000000000000000000", "0", 18), 0, 0, false, false,
                new("1000000", "3000000", "0"), new("1000000", "3000000", "0"));
            var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, asset.OriginAddress, 2, intent.RecipientProtocolAddress, 8, "0"));
            var bytes = new byte[123 + payload.Length]; bytes[0] = 1; bytes[5] = 1; bytes[7] = 1; bytes[39] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), 3);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(72), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(80), 2); bytes.AsSpan(82, 32).Fill(0xbb);
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(114), 25); payload.CopyTo(bytes, 123);
            var vaa = WttVaaCodec.Parse(bytes);
            var sequence = WttAlgorandTargetWire.SequenceAddress(9, vaa);
            rpc.ReadOverride = request => request.RequestUri!.AbsolutePath == "/v2/accounts/" + sequence
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : request.RequestUri.AbsolutePath == "/v2/blocks/500" ? Json(new { block = new
                    { rnd = 500, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), gh = WttAlgorandSnapshotTests.Rpc.Genesis } }) : RecipientAccount(request);
            var proofs = new WttTargetProofs(new(vaa, new("untrusted", "wrong", "wrong", 3, 3, [], 0, 0, "none", default, 0, "")), null);
            var quoted = await Provider.QuoteWttTargetAsync(intent, proofs);
            Assert.False(quoted.IsError, quoted.Message);
            return (intent, proofs, quoted.Result!);
        }
    }
}
