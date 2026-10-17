using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttSourceModule.ObserveWttSourceAsync"/>
    public async Task<AZOAResult<WttStageObservation>> ObserveWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (_, plan, wire) = await ReadWttSourcePreparationAsync(intent, quote, prepared, ct);
        WttStageObservation Pending(string detail) => new("Pending", prepared.TransactionId, prepared.ProtocolIdentity, Detail: detail);
        try
        {
            var transaction = await ReadWttFinalizedTransactionAsync(prepared.TransactionId, ct);
            if (transaction.ValueKind == JsonValueKind.Null)
            {
                var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
                return Ok(height.GetInt64() > wire.Recovery.LastValidBlockHeight
                    ? new WttStageObservation("NeedsOperator", prepared.TransactionId, prepared.ProtocolIdentity,
                        Detail: "Expired blockhash with missing archival history does not prove absence of publication.")
                    : Pending("The exact saved publication transaction is not finalized."));
            }
            var finalized = await RequireWttFinalityAsync(transaction, prepared, wire, plan.LookupTables, ct);
            var observed = finalized.Accounts; var meta = finalized.Meta; var slot = finalized.Slot;
            var fee = finalized.Fee; var blockIdentity = finalized.BlockIdentity;
            if (finalized.Failed)
                return Ok(new WttStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                    JsonSerializer.Serialize(new { blockIdentity, noProtocolEffect = true }), NetworkFeePaid: fee.ToString(CultureInfo.InvariantCulture)));
            var messageKey = WttSourceRole(wire.StageKeys, prepared.Stage, "message");
            var response = await DurableRpcAsync("getAccountInfo", [messageKey.Key,
                new { encoding = "base64", commitment = "finalized", minContextSlot = slot }], ct);
            if (response.GetProperty("context").GetProperty("slot").GetUInt64() < slot)
                throw new InvalidDataException("Publication account snapshot predates the finalized source transaction.");
            var account = SolanaAccountData.ReadOwned(response.GetProperty("value"), plan.Core, false);
            await RequireWttMessageUnmodifiedAsync(messageKey.Key, prepared.TransactionId, slot, ct);
            var publication = WttSolanaPublication.Read(account, intent, prepared.Stage, prepared.TransactionId, blockIdentity, messageKey.Key);
            if (prepared.Stage == "Transfer") RequireWttSourceTokenEffect(intent, observed.Keys, meta);
            var bridgePayer = WttSourceRole(wire.StageKeys, prepared.Stage, "payer").Key;
            var payerIndex = observed.Keys.Select((key, index) => (key, index)).Single(value => value.key.Key == bridgePayer).index;
            var retained = meta.GetProperty("postBalances")[payerIndex].GetUInt64();
            return Ok(new WttStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { blockIdentity, message = messageKey.Key, bridgePayer, retainedLamports = retained.ToString(CultureInfo.InvariantCulture),
                    refundDestination = intent.SourceAuthority.Address }), publication, fee.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or FormatException
            or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Ok(new WttStageObservation("NeedsOperator", prepared.TransactionId, prepared.ProtocolIdentity, Detail: exception.Message)); }
    }

    private Task<JsonElement> ReadWttFinalizedTransactionAsync(string signature, CancellationToken ct)
        => ReadSolanaFinalizedTransactionAsync(signature, ct);

    private async Task RequireWttMessageUnmodifiedAsync(string message, string originalSignature, ulong originalSlot, CancellationToken ct)
    {
        string? before = null; var seen = new HashSet<string>(StringComparer.Ordinal); var previousSlot = ulong.MaxValue;
        while (true)
        {
            var options = new Dictionary<string, object> { ["commitment"] = "finalized", ["limit"] = 1000, ["minContextSlot"] = originalSlot };
            if (before is not null) options["before"] = before;
            var history = await DurableRpcAsync("getSignaturesForAddress", [message, options], ct);
            if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() == 0)
                throw new InvalidDataException("Complete reliable-message history back to its original publication is unavailable.");
            foreach (var entry in history.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var signature = entry.GetProperty("signature").GetString()!; var slot = entry.GetProperty("slot").GetUInt64();
                if (string.IsNullOrEmpty(signature) || !seen.Add(signature) || slot > previousSlot || slot < originalSlot
                    || entry.GetProperty("confirmationStatus").GetString() != "finalized")
                    throw new InvalidDataException("Publication history is incomplete or has contradictory finalized ordering.");
                previousSlot = slot; before = signature;
                if (signature == originalSignature)
                {
                    if (slot != originalSlot || entry.GetProperty("err").ValueKind != JsonValueKind.Null)
                        throw new InvalidDataException("Original publication history contradicts its finalized successful transaction.");
                    return;
                }
                var later = await ReadWttFinalizedTransactionAsync(signature, ct);
                if (later.ValueKind != JsonValueKind.Object || later.GetProperty("slot").GetUInt64() != slot)
                    throw new InvalidDataException("Later reliable-message transaction history is unavailable.");
                var roles = SolanaObservedTransaction.Read(later, signature);
                var index = roles.Keys.Select((key, position) => (key, position)).Single(value => value.key.Key == message).position;
                if (later.GetProperty("meta").GetProperty("err").ValueKind == JsonValueKind.Null && roles.Writable(index))
                    throw new InvalidDataException("Reliable message was writable after publication; its current contents are not original publication proof.");
            }
        }
    }

    private static void RequireWttSourceTokenEffect(WttOperationIntent intent, IReadOnlyList<PublicKey> keys, JsonElement meta)
    {
        var mint = new PublicKey(intent.Asset.TokenAddress); var owner = new PublicKey(intent.SourceAuthority.Address);
        var source = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint);
        var sourceIndex = keys.Select((key, index) => (key, index)).Single(value => value.key.Key == source.Key).index;
        var pre = WttTokenBalance(meta.GetProperty("preTokenBalances"), sourceIndex, mint.Key, owner.Key, intent.Asset.Decimals, intent.Asset.NativeAsset);
        var post = WttTokenBalance(meta.GetProperty("postTokenBalances"), sourceIndex, mint.Key, owner.Key, intent.Asset.Decimals, false);
        var amount = new BigInteger(UInt64Text.Parse(intent.Amount.Debited));
        if (new BigInteger(pre) - post != (intent.Asset.NativeAsset ? BigInteger.Zero : amount))
            throw new InvalidDataException("Source token balance change differs from the exact authorized amount.");
        if (intent.Asset.OriginChain != 1) return;
        var bridge = new PublicKey(intent.Source.TokenBridge); var custody = WttSolanaAccounts.Derive(bridge, mint.KeyBytes);
        var custodyOwner = WttSolanaAccounts.Derive(bridge, "custody_signer"u8.ToArray()).Key;
        var custodyIndex = keys.Select((key, index) => (key, index)).Single(value => value.key.Key == custody.Key).index;
        var old = WttTokenBalance(meta.GetProperty("preTokenBalances"), custodyIndex, mint.Key, custodyOwner, intent.Asset.Decimals, true);
        var current = WttTokenBalance(meta.GetProperty("postTokenBalances"), custodyIndex, mint.Key, custodyOwner, intent.Asset.Decimals, false);
        if (new BigInteger(current) - old != amount) throw new InvalidDataException("Original token custody did not receive the exact locked amount.");
    }

    private static ulong WttTokenBalance(JsonElement balances, int accountIndex, string mint, string owner, byte decimals, bool allowMissing)
        => SolanaTokenBalanceEvidence.Read(balances, accountIndex, mint, owner, decimals, allowMissing);
}
