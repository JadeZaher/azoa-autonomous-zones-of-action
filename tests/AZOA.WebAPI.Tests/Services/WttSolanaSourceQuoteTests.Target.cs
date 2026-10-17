using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Solnet.Programs;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttSolanaSourceQuoteTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-owner")]
    [InlineData("changed-body")]
    [InlineData("wrong-pda")]
    [InlineData("stale-finality")]
    [InlineData("changed-emitter")]
    [InlineData("wallet-instead-of-ata")]
    public async Task PostedExactBodySurvivesOldGuardianRotationButNotChangedTargetEvidence(string mutation)
    {
        using var rpc = new SourceRpc(); var source = await rpc.IntentAsync();
        var (provider, intent, proofs) = rpc.Target(source, mutation);
        var quote = await provider.QuoteWttTargetAsync(intent, proofs);
        if (mutation != "valid") { Assert.True(quote.IsError, quote.Message); return; }
        Assert.False(quote.IsError, quote.Message);
        Assert.Equal(new[] { "Redeem", "Cleanup:Redeem" }, quote.Result!.Stages.Select(value => value.Stage));
        Assert.All(quote.Result.Stages, value => Assert.Equal("0", value.Value));
        Assert.Single(rpc.Simulated);
        Assert.Empty(rpc.Sent);
    }

    [Theory]
    [InlineData("valid", "Confirmed")]
    [InlineData("wrong-credit", "NeedsOperator")]
    [InlineData("wrong-custody", "NeedsOperator")]
    [InlineData("missing-claim", "NeedsOperator")]
    [InlineData("missing-fee", "NeedsOperator")]
    [InlineData("failed", "FailedOnChain")]
    public async Task TargetSavedWireRequiresActualClaimAndBothSidesOfTheTokenMovement(string mutation, string expected)
    {
        using var rpc = new SourceRpc(); var source = await rpc.IntentAsync();
        var (provider, intent, proofs) = rpc.Target(source, "valid");
        var quote = await provider.QuoteWttTargetAsync(intent, proofs); Assert.False(quote.IsError, quote.Message);
        using var signing = new SigningFixture(rpc, intent.TargetAuthority.Context);
        signing.LoseCommittedResponse = true;
        await Assert.ThrowsAsync<IOException>(() => signing.Provider.PrepareWttTargetAsync(intent, proofs, quote.Result!, "Redeem", 0));
        signing.AllowCustody = false; var count = signing.CustodyCalls;
        var prepared = await signing.Provider.PrepareWttTargetAsync(intent, proofs, quote.Result!, "Redeem", 0);
        Assert.False(prepared.IsError, prepared.Message); Assert.Equal(count, signing.CustodyCalls);
        rpc.LoseSendResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => signing.Provider.SubmitWttTargetAsync(intent, proofs, quote.Result!, prepared.Result!));
        await signing.Provider.SubmitWttTargetAsync(intent, proofs, quote.Result!, prepared.Result!);
        Assert.Equal(rpc.Sent[0], rpc.Sent[1]); Assert.Equal(count, signing.CustodyCalls);
        rpc.FinalizeRedemption(intent, proofs, prepared.Result!, mutation);
        var observed = await signing.Provider.ObserveWttTargetAsync(intent, proofs, quote.Result!, prepared.Result!);
        Assert.False(observed.IsError, observed.Message); Assert.Equal(expected, observed.Result!.Verdict);
        if (expected == "Confirmed")
        {
            Assert.Equal("5000", observed.Result.NetworkFeePaid);
            using var evidence = JsonDocument.Parse(observed.Result.EvidenceJson!);
            Assert.True(evidence.RootElement.GetProperty("redemptionCompleted").GetBoolean());
            Assert.Equal("123", evidence.RootElement.GetProperty("retainedLamports").GetString());
        }
    }

    [Fact]
    public async Task PostUsesThePersistedVerificationKeyAsReadonlyReferenceWithoutSigningAsIt()
    {
        using var rpc = new SourceRpc(); var source = await rpc.IntentAsync();
        var (_, intent, proofs) = rpc.Target(source, "valid");
        var provider = rpc.MakeTargetUnposted(intent, proofs);
        var quote = await provider.QuoteWttTargetAsync(intent, proofs); Assert.False(quote.IsError, quote.Message);
        Assert.Equal(new[] { "Verify:Transfer:0", "Cleanup:Verify:Transfer:0", "Post:Transfer", "Cleanup:Post:Transfer", "Redeem", "Cleanup:Redeem" },
            quote.Result!.Stages.Select(value => value.Stage));
        using var signing = new SigningFixture(rpc, intent.TargetAuthority.Context);
        var verified = await signing.Provider.PrepareWttTargetAsync(intent, proofs, quote.Result, "Verify:Transfer:0", 0);
        Assert.False(verified.IsError, verified.Message);
        using var verification = JsonDocument.Parse(verified.Result!.RecoveryMetadata);
        var firstKeys = JsonSerializer.Deserialize<Dictionary<string, WttStageKeyBinding>>(verification.RootElement.GetProperty("StageKeys").GetRawText())!;
        var verificationKey = firstKeys.Single(value => value.Value.StageRole.EndsWith(":signature-set", StringComparison.Ordinal));
        rpc.AcceptVerification(proofs.Transfer.Vaa, verificationKey.Key);
        var posted = await signing.Provider.PrepareWttTargetAsync(intent, proofs, quote.Result, "Post:Transfer", 0);
        Assert.False(posted.IsError, posted.Message);
        using var posting = JsonDocument.Parse(posted.Result!.RecoveryMetadata);
        var secondKeys = JsonSerializer.Deserialize<Dictionary<string, WttStageKeyBinding>>(posting.RootElement.GetProperty("StageKeys").GetRawText())!;
        Assert.Equal(verificationKey, secondKeys.Single(value => value.Value.StageRole.EndsWith(":signature-set", StringComparison.Ordinal)));
        var message = SolanaMessageEnvelope.Read(Convert.FromBase64String(posting.RootElement.GetProperty("MessageBase64").GetString()!));
        Assert.Equal(2, message.RequiredSignatures);
        Assert.DoesNotContain(message.RequiredKeys, value => value.Key == verificationKey.Key);
        Assert.Equal(3, signing.Keys.Count);
    }

    private sealed partial class SourceRpc
    {
        private WttProofVerifier? _targetVerifier;

        public SolanaProvider MakeTargetUnposted(WttOperationIntent intent, WttTargetProofs proofs)
        {
            _accounts.Remove(WttSolanaCoreInstructions.PostedVaa(_core, proofs.Transfer.Vaa).Key);
            var bridge = new byte[24]; BinaryPrimitives.WriteUInt32LittleEndian(bridge, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(bridge.AsSpan(16), 17);
            _accounts[WttSolanaAccounts.Derive(_core, "Bridge"u8.ToArray()).Key] = AccountData(bridge, _core.Key);
            var guardians = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(guardians, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(guardians.AsSpan(4), 1); guardians.AsSpan(8, 20).Fill(0xaa);
            BinaryPrimitives.WriteUInt32LittleEndian(guardians.AsSpan(32), uint.MaxValue);
            _accounts[WttSolanaCoreInstructions.GuardianSet(_core, 1).Key] = AccountData(guardians, _core.Key);
            var signatures = new Mock<IWttGuardianSignatureVerifier>(MockBehavior.Strict);
            signatures.Setup(value => value.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>())).Returns(true);
            _targetVerifier = new(signatures.Object, TimeProvider.System,
                Options.Create(new WttFinalityOptions { Networks = [new(intent.Target.ProtocolIdentity, 90)] }));
            return new(_configuration, NullLogger<SolanaProvider>.Instance, null, null, null, this, wttProofVerifier: _targetVerifier);
        }

        public void AcceptVerification(WttVaa vaa, string signatureSet)
        {
            var data = new byte[41]; BinaryPrimitives.WriteUInt32LittleEndian(data, 1); data[4] = 1;
            WttSolanaCoreInstructions.BodyHash(vaa).CopyTo(data, 5);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(37), vaa.GuardianSetIndex);
            _accounts[signatureSet] = AccountData(data, _core.Key);
        }

        public void FinalizeRedemption(WttOperationIntent intent, WttTargetProofs proofs, WttStagePreparation prepared, string mutation)
        {
            publicationSignature = prepared.TransactionId;
            using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
            var stageKeys = JsonSerializer.Deserialize<Dictionary<string, WttStageKeyBinding>>(recovery.RootElement.GetProperty("StageKeys").GetRawText())!;
            var payer = stageKeys.Single(pair => pair.Value.StageRole.EndsWith(":payer", StringComparison.Ordinal)).Key;
            var message = SolanaMessageEnvelope.Read(Convert.FromBase64String(recovery.RootElement.GetProperty("MessageBase64").GetString()!));
            var keys = SolanaMessageEnvelope.Resolve(message, [TableSnapshot]).Select(value => value.Key).ToArray();
            var recipientIndex = Array.IndexOf(keys, AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(Owner, _mint).Key);
            var custodyIndex = Array.IndexOf(keys, WttSolanaAccounts.Derive(_token, _mint.KeyBytes).Key);
            var custodyOwner = WttSolanaAccounts.Derive(_token, "custody_signer"u8.ToArray()).Key;
            object Balance(int index, string owner, string amount) => new
            {
                accountIndex = index, mint = _mint.Key, owner, programId = TokenProgram.ProgramIdKey.Key,
                uiTokenAmount = new { amount, decimals = intent.TargetDecimals },
            };
            var balances = Enumerable.Repeat(1000000UL, keys.Length).ToArray(); balances[Array.IndexOf(keys, payer)] = 123;
            var loaded = message.Lookups.Sum(value => value.WritableIndices.Length);
            var redeemed = ulong.Parse(WttPayloadCodec.Denormalize(intent.Amount.Amount, intent.TargetDecimals), System.Globalization.CultureInfo.InvariantCulture);
            FinalizedTransaction = new
            {
                slot = 100UL, transaction = new[] { prepared.SignedTransactionBase64, "base64" },
                meta = new
                {
                    err = mutation == "failed" ? (object)new { InstructionError = new object[] { 4, 1 } } : null,
                    fee = mutation == "missing-fee" ? (ulong?)null : 5000UL,
                    loadedAddresses = new { writable = keys.Skip(message.StaticKeys.Count).Take(loaded).ToArray(),
                        @readonly = keys.Skip(message.StaticKeys.Count + loaded).ToArray() },
                    preTokenBalances = new[] { Balance(recipientIndex, Owner.Key, "0"), Balance(custodyIndex, custodyOwner, "1000000") },
                    postTokenBalances = new[] { Balance(recipientIndex, Owner.Key, (mutation == "wrong-credit" ? redeemed - 1 : redeemed).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Balance(custodyIndex, custodyOwner, (1000000 - redeemed + (mutation == "wrong-custody" ? 1UL : 0UL)).ToString(System.Globalization.CultureInfo.InvariantCulture)) },
                    postBalances = balances,
                },
            };
            var vaa = proofs.Transfer.Vaa;
            if (mutation != "missing-claim")
                _accounts[WttSolanaAccounts.Claim(_token, vaa.EmitterChain, Convert.FromHexString(vaa.EmitterAddress), ulong.MaxValue).Key]
                    = AccountData([1], _token.Key);
        }
        public (SolanaProvider Provider, WttOperationIntent Intent, WttTargetProofs Proofs) Target(WttOperationIntent source, string mutation)
        {
            var target = source.Source; var remote = source.Target;
            var recipientAta = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(Owner, _mint);
            var intent = source with
            {
                Source = remote, Target = target, RecipientAddress = Owner.Key,
                RecipientProtocolAddress = Convert.ToHexStringLower(recipientAta.KeyBytes), TargetAssetAddress = _mint.Key,
                RequiresAttestation = false,
                TargetDecimals = source.Asset.Decimals, UnwrapNative = _mint.Key == SolanaSeededNativeAccount.Mint.Key,
                TargetAuthority = new(Owner.Key, source.SourceAuthority.Context with { Scope = AzoaScopes.WttTargetSign }),
            };
            if (mutation == "wallet-instead-of-ata") intent = intent with { RecipientProtocolAddress = Convert.ToHexStringLower(Owner.KeyBytes) };
            var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, 1,
                intent.RecipientProtocolAddress, 1, intent.Amount.Fee));
            var envelope = new byte[6 + 66 + 51 + payload.Length]; envelope[0] = 1; envelope[5] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1), 1);
            envelope.AsSpan(7, 32).Fill(1); envelope.AsSpan(39, 32).Fill(2);
            var body = envelope.AsSpan(72); BinaryPrimitives.WriteUInt32BigEndian(body, 100);
            BinaryPrimitives.WriteUInt32BigEndian(body[4..], intent.Nonce);
            BinaryPrimitives.WriteUInt16BigEndian(body[8..], remote.ProtocolChain);
            Convert.FromHexString(remote.EmitterAddressHex).CopyTo(body[10..]);
            BinaryPrimitives.WriteUInt64BigEndian(body[42..], ulong.MaxValue); body[50] = intent.ConsistencyLevel; payload.CopyTo(body[51..]);
            var vaa = WttVaaCodec.Parse(envelope);
            var posted = new byte[95 + payload.Length]; "vaa"u8.CopyTo(posted); posted[3] = 1; posted[4] = vaa.ConsistencyLevel;
            BinaryPrimitives.WriteUInt32LittleEndian(posted.AsSpan(5), vaa.Timestamp); new Account().PublicKey.KeyBytes.CopyTo(posted, 9);
            BinaryPrimitives.WriteUInt32LittleEndian(posted.AsSpan(41), 101); BinaryPrimitives.WriteUInt32LittleEndian(posted.AsSpan(45), vaa.Nonce);
            BinaryPrimitives.WriteUInt64LittleEndian(posted.AsSpan(49), ulong.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(posted.AsSpan(57), vaa.EmitterChain);
            Convert.FromHexString(vaa.EmitterAddress).CopyTo(posted, 59);
            BinaryPrimitives.WriteUInt32LittleEndian(posted.AsSpan(91), (uint)payload.Length); payload.CopyTo(posted, 95);
            if (mutation == "changed-body") posted[^1] ^= 1;
            var postedAddress = mutation == "wrong-pda" ? new Account().PublicKey : WttSolanaCoreInstructions.PostedVaa(_core, vaa);
            _accounts[postedAddress.Key] = AccountData(posted, mutation == "wrong-owner" ? _token.Key : _core.Key);
            var chain = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(chain, vaa.EmitterChain);
            var endpoint = new byte[34]; BinaryPrimitives.WriteUInt16LittleEndian(endpoint, vaa.EmitterChain);
            Convert.FromHexString(vaa.EmitterAddress).CopyTo(endpoint, 2);
            if (mutation == "changed-emitter") endpoint[2] ^= 1;
            _accounts[WttSolanaAccounts.Derive(_token, chain, Convert.FromHexString(vaa.EmitterAddress)).Key] = AccountData(endpoint, _token.Key);
            if (mutation == "stale-finality")
            {
                var clock = new byte[40]; BinaryPrimitives.WriteUInt64LittleEndian(clock, 90);
                BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(32), DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
                _accounts[SysVars.ClockKey.Key] = AccountData(clock, "Sysvar1111111111111111111111111111111111111");
            }
            var verifier = new WttProofVerifier(new Mock<IWttGuardianSignatureVerifier>(MockBehavior.Strict).Object,
                TimeProvider.System, Options.Create(new WttFinalityOptions { Networks = [new(target.ProtocolIdentity, 90)] }));
            _targetVerifier = verifier;
            var provider = new SolanaProvider(_configuration, NullLogger<SolanaProvider>.Instance, null, null, null, this, wttProofVerifier: verifier);
            var historical = new WttGuardianEvidence(target.ProtocolIdentity, target.CoreBridge, target.TokenBridge,
                1, 9, [new string('a', 40)], 102, 101, "historical", DateTimeOffset.UnixEpoch, vaa.EmitterChain, vaa.EmitterAddress);
            return (provider, intent, new(new(vaa, historical), null));
        }
    }
}
