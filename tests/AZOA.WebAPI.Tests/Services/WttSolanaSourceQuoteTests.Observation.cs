using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Programs;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttSolanaSourceQuoteTests
{
    [Theory]
    [InlineData("valid", "Confirmed")]
    [InlineData("wrong-custody-delta", "NeedsOperator")]
    [InlineData("changed-publication", "NeedsOperator")]
    [InlineData("missing-fee", "NeedsOperator")]
    [InlineData("missing-history", "NeedsOperator")]
    [InlineData("duplicate-block-signature", "NeedsOperator")]
    [InlineData("failed", "FailedOnChain")]
    public async Task PublicationNeedsExactFinalizedWireBodyFeeAndActualTokenEffect(string mutation, string verdict)
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quote = (await rpc.Provider.QuoteWttSourceAsync(intent)).Result!;
        using var signing = new SigningFixture(rpc, intent.SourceAuthority.Context);
        var prepared = await signing.Provider.PrepareWttSourceAsync(intent, quote, "Transfer", 0);
        Assert.False(prepared.IsError, prepared.Message);
        rpc.FinalizePublication(intent, prepared.Result!, mutation);
        var result = await signing.Provider.ObserveWttSourceAsync(intent, quote, prepared.Result!);
        Assert.False(result.IsError, result.Message); Assert.Equal(verdict, result.Result!.Verdict);
        if (verdict == "Confirmed")
        {
            Assert.Equal("5000", result.Result.NetworkFeePaid); Assert.NotNull(result.Result.Publication);
            Assert.Equal("18446744073709551615", result.Result.Publication.Sequence);
            using var evidence = JsonDocument.Parse(result.Result.EvidenceJson!);
            Assert.Equal("123", evidence.RootElement.GetProperty("retainedLamports").GetString());
            Assert.Equal(intent.SourceAuthority.Address, evidence.RootElement.GetProperty("refundDestination").GetString());
        }
        else Assert.Null(result.Result.Publication);
    }

    private sealed partial class SourceRpc
    {
        public object? FinalizedTransaction { get; private set; }
        private string? publicationSignature;
        private string? publicationAddress;
        private byte[]? publicationBytes;
        private string? observationMutation;

        public void FinalizePublication(WttOperationIntent intent, WttStagePreparation prepared, string mutation,
            ulong payerBefore = 0, ulong payerAfter = 123)
        {
            observationMutation = mutation; publicationSignature = prepared.TransactionId;
            using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
            var keys = JsonSerializer.Deserialize<Dictionary<string, WttStageKeyBinding>>(recovery.RootElement.GetProperty("StageKeys").GetRawText())!;
            publicationAddress = keys.Single(pair => pair.Value.StageRole == "Source:Transfer:message").Key;
            var bridgePayer = keys.Single(pair => pair.Value.StageRole == "Source:Transfer:payer").Key;
            var messageBytes = Convert.FromBase64String(recovery.RootElement.GetProperty("MessageBase64").GetString()!);
            var message = SolanaMessageEnvelope.Read(messageBytes);
            var resolved = SolanaMessageEnvelope.Resolve(message, [TableSnapshot]);
            var all = resolved.Select(key => key.Key).ToArray();
            var sourceIndex = Array.IndexOf(all, AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(Owner, _mint).Key);
            var custodyIndex = Array.IndexOf(all, WttSolanaAccounts.Derive(_token, _mint.KeyBytes).Key);
            var custodyOwner = WttSolanaAccounts.Derive(_token, "custody_signer"u8.ToArray()).Key;
            object Balance(int index, string owner, string amount) => new
            {
                accountIndex = index, mint = _mint.Key, owner, programId = TokenProgram.ProgramIdKey.Key,
                uiTokenAmount = new { amount, decimals = 6 },
            };
            var balances = Enumerable.Repeat(1000000UL, all.Length).ToArray(); balances[Array.IndexOf(all, bridgePayer)] = payerAfter;
            var priorBalances = balances.ToArray(); priorBalances[Array.IndexOf(all, bridgePayer)] = payerBefore;
            var loadedWritableCount = message.Lookups.Sum(lookup => lookup.WritableIndices.Length);
            FinalizedTransaction = new
            {
                slot = 100UL, transaction = new[] { prepared.SignedTransactionBase64, "base64" },
                meta = new
                {
                    err = mutation == "failed" ? (object)new { InstructionError = new object[] { 4, 1 } } : null,
                    fee = mutation == "missing-fee" ? (ulong?)null : 5000UL,
                    loadedAddresses = new
                    {
                        writable = all.Skip(message.StaticKeys.Count).Take(loadedWritableCount).ToArray(),
                        @readonly = all.Skip(message.StaticKeys.Count + loadedWritableCount).ToArray(),
                    },
                    preTokenBalances = new[] { Balance(sourceIndex, Owner.Key, "1000000") },
                    postTokenBalances = new[] { Balance(sourceIndex, Owner.Key, "999900"),
                        Balance(custodyIndex, custodyOwner, mutation == "wrong-custody-delta" ? "99" : "100") },
                    preBalances = priorBalances, postBalances = balances,
                },
            };
            var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, 1,
                intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee));
            publicationBytes = new byte[95 + payload.Length]; "msg"u8.CopyTo(publicationBytes); publicationBytes[4] = 32;
            BinaryPrimitives.WriteUInt32LittleEndian(publicationBytes.AsSpan(41), 1234);
            BinaryPrimitives.WriteUInt32LittleEndian(publicationBytes.AsSpan(45), intent.Nonce);
            BinaryPrimitives.WriteUInt64LittleEndian(publicationBytes.AsSpan(49), ulong.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(publicationBytes.AsSpan(57), 1);
            Convert.FromHexString(intent.Source.EmitterAddressHex).CopyTo(publicationBytes, 59);
            BinaryPrimitives.WriteUInt32LittleEndian(publicationBytes.AsSpan(91), (uint)payload.Length);
            payload.CopyTo(publicationBytes, 95); if (mutation == "changed-publication") publicationBytes[^1] ^= 1;
        }

        private object FinalizedBlock() => new
        {
            blockhash = "finalized-source-block",
            signatures = observationMutation == "duplicate-block-signature" ? new[] { publicationSignature, publicationSignature } : new[] { publicationSignature },
        };

        private object PublicationAccount(JsonElement args)
        {
            if (publicationAddress is null)
                return new { context = new { slot = 100 }, value = _accounts.GetValueOrDefault(args[0].GetString()!) };
            Assert.Equal(publicationAddress, args[0].GetString());
            return new { context = new { slot = 100 }, value = AccountData(publicationBytes!, _core.Key) };
        }

        private object PublicationHistory(JsonElement args)
        {
            Assert.Equal(publicationAddress, args[0].GetString());
            return observationMutation == "missing-history" ? Array.Empty<object>()
                : new object[] { new { signature = publicationSignature, slot = 100UL, confirmationStatus = "finalized", err = (object?)null } };
        }
    }
}
