using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using Solnet.Programs;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private sealed record JupiterReleaseEvent(ulong Slot, int Transaction, int Instruction, int InnerInstruction, string Signature);
    private sealed record JupiterReleaseCursor(int Version, string Binding, ulong NextSlot, string ExpectedHash,
        ulong ExpectedHeight, ulong Blocks, string Transcript, JupiterReleaseEvent? Earliest, bool Complete);

    /// <inheritdoc cref="IDexTransactionModule.ScanSwapRentReleaseAsync"/>
    public async Task<AZOAResult<SwapRentReleaseProgress>> ScanSwapRentReleaseAsync(SwapIntent intent,
        PreparedSwapStage allocation, PreparedSwapStage cleanup, string? checkpointJson, CancellationToken ct = default)
    {
        if (allocation.Plan.Purpose != "Effect" || cleanup.Plan.Purpose != "Cleanup" || cleanup.Plan.CleanupConsent is not { } consent)
            return AZOAResult<SwapRentReleaseProgress>.Failure("Rent release requires the exact original allocation and owner cleanup.");
        var original = await ObserveSwapStageAsync(intent, allocation, ct);
        var closed = await ObserveSwapStageAsync(intent, cleanup, ct);
        if (original.IsError || closed.IsError || original.Result is not { Verdict: "Confirmed", EvidenceJson: not null } originalProof ||
            closed.Result is not { Verdict: "Confirmed", EvidenceJson: not null } cleanupProof)
            return AZOAResult<SwapRentReleaseProgress>.Failure("Finalized allocation and cleanup evidence remain unresolved.");
        try
        {
            using var receipt = JsonDocument.Parse(originalProof.EvidenceJson);
            var settlement = receipt.RootElement.GetProperty("Settlement").Deserialize<JupiterSettlement>()
                ?? throw new InvalidDataException("Original allocation receipt is missing.");
            var account = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(new PublicKey(intent.Terms.Owner.Address), new PublicKey(consent.AssetId)).Key;
            var effects = settlement.RetainedEffects.Where(effect => effect.Kind == "CreateAta" && effect.AssetId == consent.AssetId &&
                effect.DestinationAddress == account && effect.OwnerAddress == intent.Terms.Owner.Address).ToArray();
            if (effects.Length != 1) throw new InvalidDataException("The original settlement did not attribute this allocation.");
            var amount = UInt64Text.Parse(effects[0].Amount);
            var firstTx = await ReadSolanaFinalizedTransactionAsync(allocation.TransactionIds.Single(), ct);
            var lastTx = await ReadSolanaFinalizedTransactionAsync(cleanup.TransactionIds.Single(), ct);
            var firstSlot = firstTx.GetProperty("slot").GetUInt64(); var lastSlot = lastTx.GetProperty("slot").GetUInt64();
            if (firstSlot > lastSlot) throw new InvalidDataException("Cleanup precedes its original allocation.");
            var firstBlock = await ReadJupiterHistoryBlockAsync(firstSlot, ct);
            var lastBlock = firstSlot == lastSlot ? firstBlock : await ReadJupiterHistoryBlockAsync(lastSlot, ct);
            var firstHeader = JupiterBlockHeader(firstBlock, firstSlot); var lastHeader = JupiterBlockHeader(lastBlock, lastSlot);
            using var cleanupReceipt = JsonDocument.Parse(cleanupProof.EvidenceJson);
            if (receipt.RootElement.GetProperty("BlockIdentity").GetString() != firstSlot.ToString(CultureInfo.InvariantCulture) + ":" + firstHeader.Hash ||
                cleanupReceipt.RootElement.GetProperty("BlockIdentity").GetString() != lastSlot.ToString(CultureInfo.InvariantCulture) + ":" + lastHeader.Hash)
                throw new InvalidDataException("Rent history boundaries differ from their authoritative settlement blocks.");
            var firstIndex = ExactJupiterBlockPosition(firstBlock, allocation);
            var lastIndex = ExactJupiterBlockPosition(lastBlock, cleanup);
            if (firstSlot == lastSlot && firstIndex >= lastIndex)
                throw new InvalidDataException("Cleanup does not follow its allocation in finalized execution order.");
            var originalAccounts = SolanaObservedTransaction.Read(firstTx, allocation.TransactionIds.Single());
            if (JupiterCloseHistory.FirstClose(originalAccounts, firstTx.GetProperty("meta"), account) is not null)
                throw new InvalidDataException("The allocation transaction contains an unresolved close and recreation history.");
            var binding = CanonicalJson.Hash(new { intent, allocation, cleanup, firstSlot, lastSlot, firstIndex, lastIndex,
                FirstHash = firstHeader.Hash, LastHash = lastHeader.Hash, amount });
            var cursor = checkpointJson is null ? new JupiterReleaseCursor(1, binding, lastSlot, lastHeader.Hash,
                lastHeader.Height, 0, binding, null, false) : JsonSerializer.Deserialize<JupiterReleaseCursor>(checkpointJson);
            if (cursor is null || cursor.Version != 1 || cursor.Binding != binding || cursor.Complete ||
                cursor.NextSlot < firstSlot || cursor.NextSlot > lastSlot || cursor.Blocks > lastHeader.Height ||
                cursor.ExpectedHeight != lastHeader.Height - cursor.Blocks || cursor.Transcript is not { Length: 64 })
                throw new InvalidDataException("Saved rent-release history differs from its fixed finalized boundaries.");
            for (var pass = 0; pass < 8 && !cursor.Complete; pass++)
            {
                var block = cursor.NextSlot == lastSlot ? lastBlock : cursor.NextSlot == firstSlot ? firstBlock
                    : await ReadJupiterHistoryBlockAsync(cursor.NextSlot, ct);
                var header = JupiterBlockHeader(block, cursor.NextSlot);
                if (header.Hash != cursor.ExpectedHash || header.Height != cursor.ExpectedHeight)
                    throw new InvalidDataException("Rent-release finalized ancestry changed.");
                var transactions = block.GetProperty("transactions");
                if (transactions.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Complete transaction bodies are required for rent-release history.");
                var upper = cursor.NextSlot == lastSlot ? lastIndex : transactions.GetArrayLength() - 1;
                var lower = cursor.NextSlot == firstSlot ? firstIndex + 1 : 0;
                var earliest = cursor.Earliest;
                for (var index = upper; index >= lower; index--)
                {
                    var transaction = transactions[index];
                    var wire = Convert.FromBase64String(transaction.GetProperty("transaction")[0].GetString()!);
                    if (wire.Length < 65 || wire[0] is 0 or > 127)
                        throw new InvalidDataException("Historical transaction signature framing is invalid.");
                    var signature = Encoders.Base58.EncodeData(wire.AsSpan(1, 64).ToArray());
                    var accounts = SolanaObservedTransaction.Read(transaction, signature);
                    var position = JupiterCloseHistory.FirstClose(accounts, transaction.GetProperty("meta"), account);
                    if (position is not null) earliest = new(cursor.NextSlot, index, position.Instruction, position.InnerInstruction, signature);
                }
                var complete = cursor.NextSlot == firstSlot;
                if (!complete && header.Parent < firstSlot)
                    throw new InvalidDataException("Finalized ancestry skipped the proven allocation block.");
                cursor = cursor with { NextSlot = header.Parent, ExpectedHash = header.PreviousHash,
                    ExpectedHeight = header.Height - 1, Blocks = checked(cursor.Blocks + 1), Earliest = earliest, Complete = complete,
                    Transcript = CanonicalJson.Hash(new { Previous = cursor.Transcript, Slot = cursor.NextSlot, header.Hash,
                        header.PreviousHash, Transactions = transactions.GetArrayLength(), earliest }) };
            }
            var checkpoint = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(cursor));
            if (!cursor.Complete) return AZOAResult<SwapRentReleaseProgress>.Success(new(checkpoint, false));
            if (cursor.Earliest is null) throw new InvalidDataException("Complete history did not contain the proven cleanup close.");
            var evidence = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new
            {
                format = "jupiter-allocation-release/1", binding, account, amount, cursor.Earliest, cursor.Transcript, cursor.Blocks
            }));
            return AZOAResult<SwapRentReleaseProgress>.Success(new(checkpoint, true, amount.ToString(CultureInfo.InvariantCulture), evidence));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        { return AZOAResult<SwapRentReleaseProgress>.Failure("Complete finalized allocation-release history is unavailable or inconsistent."); }
    }

    private static int ExactJupiterBlockPosition(JsonElement block, PreparedSwapStage stage)
    {
        var transactions = block.GetProperty("transactions");
        if (transactions.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Finalized block omitted transactions.");
        var signed = JsonSerializer.Deserialize<string>(stage.SignedPayloadJson);
        var positions = transactions.EnumerateArray().Select((transaction, index) => (transaction, index))
            .Where(item => item.transaction.GetProperty("transaction")[0].GetString() == signed).ToArray();
        if (positions.Length != 1) throw new InvalidDataException("Exact finalized stage bytes have no unique block position.");
        return positions[0].index;
    }
}
