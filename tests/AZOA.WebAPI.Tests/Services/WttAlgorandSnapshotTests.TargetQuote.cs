using System.Buffers.Binary;
using System.Net;
using AZOA.WebAPI.Core;
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
    [Theory]
    [InlineData("exact", false)]
    [InlineData("verifier", true)]
    [InlineData("fee-cap", true)]
    [InlineData("deposit-cap", true)]
    [InlineData("proof-recipient", true)]
    [InlineData("frozen-recipient", true)]
    [InlineData("prefunded-storage", false)]
    [InlineData("consumed", true)]
    [InlineData("consumed-during-quote", true)]
    [InlineData("registration", false)]
    public async Task Target_quote_uses_actual_guardians_verifier_registration_and_recipient_without_custody(string mutation, bool rejected)
    {
        using var rpc = new Rpc(mutation == "registration" ? "unregistered" : mutation == "frozen-recipient" ? "wrapped-recipient-frozen" : "wrapped");
        if (mutation == "verifier") rpc.VerifierAddress = global::Algorand.Address.ForApplication(9).ToString();
        var signature = new Mock<IWttGuardianSignatureVerifier>();
        signature.Setup(value => value.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>())).Returns(true);
        var verifier = new WttProofVerifier(signature.Object, TimeProvider.System,
            Options.Create(new WttFinalityOptions { Networks = [new("algorand:" + Rpc.Genesis, 90)] }));
        using var services = new ServiceCollection().AddSingleton(verifier).BuildServiceProvider();
        var provider = Create(rpc, scopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        var origin = new WttAssetDescriptor("erc20", false, 18, 2, new string('c', 64), 18, new string('0', 64), new string('0', 64), "origin");
        var inspected = await provider.InspectWttTargetAsync(origin, Rpc.Recipient, false);
        Assert.False(inspected.IsError, inspected.Message);
        var target = inspected.Result!.Network;
        var actor = Guid.NewGuid();
        var context = SigningContext.ForUser(actor, Guid.NewGuid()).ForChain("Algorand") with { Scope = AzoaScopes.WttTargetSign };
        var source = target with { ChainType = "Ethereum", ProtocolIdentity = "eip155:31337", ProtocolChain = 2, EmitterAddressHex = new string('b', 64) };
        var intent = new WttOperationIntent(1, "target-quote", actor, null, source, target, origin,
            new(Rpc.Recipient, context), new(Rpc.Recipient, context), Rpc.Recipient,
            Convert.ToHexStringLower(new global::Algorand.Address(Rpc.Recipient).Bytes), mutation == "registration" ? null : ulong.MaxValue.ToString(), 8,
            WttPayloadCodec.Normalize("1000000000000000000", "0", 18), 0, 0, false, false,
            new("1000000", "3000000", "0"), new(mutation == "fee-cap" ? "0" : "1000000", mutation == "deposit-cap" ? "0" : "3000000", "0"));
        var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, origin.OriginAddress, 2,
            mutation == "proof-recipient" ? new string('a', 64) : intent.RecipientProtocolAddress, 8, "0"));
        var envelope = new byte[123 + payload.Length]; envelope[0] = 1; envelope[5] = 1; envelope[7] = 1; envelope[39] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1), 3);
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(72), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16BigEndian(envelope.AsSpan(80), 2); envelope.AsSpan(82, 32).Fill(0xbb);
        BinaryPrimitives.WriteUInt64BigEndian(envelope.AsSpan(114), 25); payload.CopyTo(envelope, 123);
        var vaa = WttVaaCodec.Parse(envelope);
        var sequenceAddress = WttAlgorandTargetWire.SequenceAddress(9, vaa);
        var sequenceReads = 0;
        rpc.ReadOverride = request => request.RequestUri!.AbsolutePath == "/v2/accounts/" + sequenceAddress
            ? SequenceAccount()
            : request.RequestUri.AbsolutePath == "/v2/blocks/500" ? Json(new { block = new
                { rnd = 500, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), gh = Rpc.Genesis } }) : null;
        var untrustedEvidence = new WttGuardianEvidence("untrusted", "wrong", "wrong", 3, 3, [], 0, 0, "none", default, 0, "");
        WttVerifiedProof? attestation = null;
        if (mutation == "registration")
        {
            var metadata = WttPayloadCodec.EncodeAttestation(new(origin.OriginAddress, 2, 18, origin.SymbolHex, origin.NameHex));
            var registration = new byte[123 + metadata.Length]; envelope.AsSpan(0, 123).CopyTo(registration);
            BinaryPrimitives.WriteUInt64BigEndian(registration.AsSpan(114), 26); metadata.CopyTo(registration, 123);
            attestation = new(WttVaaCodec.Parse(registration), untrustedEvidence);
        }
        var proofs = new WttTargetProofs(new(vaa, untrustedEvidence), attestation);
        var quote = await provider.QuoteWttTargetAsync(intent, proofs);
        Assert.Equal(rejected, quote.IsError);
        if (rejected) return;
        Assert.Equal(mutation == "registration"
            ? new[] { "AttestationSequenceStorage", "WrappedStorage", "Register", "RecipientOptIn", "Redeem" }
            : new[] { "TransferSequenceStorage", "Redeem" }, quote.Result!.Stages.Select(stage => stage.Stage));
        Assert.Equal("1002000", quote.Result.Stages[0].LockedDeposit);
        Assert.All(quote.Result.Stages, stage => Assert.Equal(Rpc.Recipient, stage.ParticipantAddress));
        Assert.True(ulong.Parse(quote.Result.Stages[^1].NetworkFee) >= 4000);
        var read = typeof(AlgorandProvider).GetMethod("ReadWttAlgoTargetQuote", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var accepted = Assert.IsType<WttAlgorandTargetPlan>(read.Invoke(provider, [intent, proofs, quote.Result]));
        Assert.Equal(mutation == "registration" ? (ulong?)null : ulong.MaxValue, accepted.Asset);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => read.Invoke(provider,
            [intent, proofs, quote.Result with { QuoteId = new string('0', 64) }]));
        signature.Verify(value => value.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>()), Times.AtLeastOnce());

        HttpResponseMessage SequenceAccount()
        {
            sequenceReads++;
            if (mutation == "prefunded-storage") return Json(new Dictionary<string, object>
                { ["address"] = sequenceAddress, ["round"] = 500, ["amount"] = 100000, ["apps-local-state"] = Array.Empty<object>() });
            if (mutation == "consumed" || mutation == "consumed-during-quote" && sequenceReads > 1)
            {
                var blob = new byte[1905]; blob[3] = 2;
                var pages = Enumerable.Range(0, 15).Select(page => new
                {
                    key = Convert.ToBase64String([(byte)page]),
                    value = new { type = 1, bytes = Convert.ToBase64String(blob.AsSpan(page * 127, 127)) }
                }).ToArray();
                return Json(new Dictionary<string, object> { ["address"] = sequenceAddress, ["round"] = 500,
                    ["auth-addr"] = global::Algorand.Address.ForApplication(9).ToString(),
                    ["apps-local-state"] = new[] { new Dictionary<string, object> { ["id"] = 9, ["key-value"] = pages } } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
