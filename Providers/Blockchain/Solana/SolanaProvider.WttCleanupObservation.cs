using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttCleanupModule.SubmitWttCleanupAsync"/>
    public async Task SubmitWttCleanupAsync(WttOperationIntent intent, WttCleanupQuote quote, WttCleanupConsent consent,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        var wire = await ReadWttCleanupPreparationAsync(intent, quote, consent, prepared, ct);
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.GetInt64() > wire.Recovery.LastValidBlockHeight) return;
        if (await NativeMessageFeeAsync(Convert.FromBase64String(wire.MessageBase64), ct) > UInt64Text.Parse(quote.NetworkFee))
            throw new InvalidDataException("Current cleanup network fee exceeds its frozen authorization.");
        var result = await DurableRpcAsync("sendTransaction", [prepared.SignedTransactionBase64,
            new { encoding = "base64", skipPreflight = false, preflightCommitment = "finalized", maxRetries = 0 }], ct);
        if (result.ValueKind != JsonValueKind.String || result.GetString() != prepared.TransactionId)
            throw new InvalidDataException("Cleanup send response differs from the saved candidate; retain ambiguous original wire.");
    }

    /// <inheritdoc cref="IWttCleanupModule.ObserveWttCleanupAsync"/>
    public async Task<AZOAResult<WttCleanupObservation>> ObserveWttCleanupAsync(WttOperationIntent intent, WttCleanupQuote quote,
        WttCleanupConsent consent, WttStagePreparation prepared, CancellationToken ct = default)
    {
        var wire = await ReadWttCleanupPreparationAsync(intent, quote, consent, prepared, ct);
        try
        {
            var transaction = await ReadWttFinalizedTransactionAsync(prepared.TransactionId, ct);
            if (transaction.ValueKind == JsonValueKind.Null)
            {
                var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
                return Ok(new WttCleanupObservation(new(height.GetInt64() > wire.Recovery.LastValidBlockHeight ? "NeedsOperator" : "Pending",
                    prepared.TransactionId, prepared.ProtocolIdentity, Detail: "Missing finalized cleanup history does not prove absence of a return."), null, null));
            }
            var finalized = await RequireWttFinalityAsync(transaction, prepared, wire, [], ct);
            if (finalized.Failed)
                return Ok(new WttCleanupObservation(new("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                    JsonSerializer.Serialize(new { finalized.BlockIdentity, noReturnEffect = true }),
                    NetworkFeePaid: finalized.Fee.ToString(CultureInfo.InvariantCulture)), "0", "0"));
            int Index(string address) => finalized.Accounts.Keys.Select((key, index) => (key, index)).Single(value => value.key.Key == address).index;
            var payer = Index(quote.StageAccount); var destination = Index(quote.DestinationAddress);
            var pre = finalized.Meta.GetProperty("preBalances"); var post = finalized.Meta.GetProperty("postBalances");
            var remaining = WttSolanaCleanupAccounting.FinalizedReturn(pre[payer].GetUInt64(), post[payer].GetUInt64(),
                pre[destination].GetUInt64(), post[destination].GetUInt64(), finalized.Fee,
                UInt64Text.Parse(quote.ReturnAmount), UInt64Text.Parse(quote.OriginalAttributedDepositReturn));
            return Ok(new WttCleanupObservation(new("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { finalized.BlockIdentity, quote.OriginalPreparationHash, quote.OriginalObservationHash,
                    quote.StageAccount, quote.DestinationAddress, quote.ReturnAmount, quote.OriginalAttributedDepositReturn,
                    remainingStageBalance = remaining.ToString(CultureInfo.InvariantCulture) }),
                NetworkFeePaid: finalized.Fee.ToString(CultureInfo.InvariantCulture)), quote.ReturnAmount,
                quote.OriginalAttributedDepositReturn, remaining.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Ok(new WttCleanupObservation(new("NeedsOperator", prepared.TransactionId, prepared.ProtocolIdentity, Detail: exception.Message), null, null)); }
    }

    private async Task<WttSolanaWirePlan> ReadWttCleanupPreparationAsync(WttOperationIntent intent, WttCleanupQuote quote,
        WttCleanupConsent consent, WttStagePreparation prepared, CancellationToken ct)
    {
        var plan = ReadWttCleanupQuote(intent, quote);
        var binding = WttCleanupBinding(intent, quote, consent, prepared.Generation);
        using var scope = _custodyScopeFactory?.CreateScope();
        var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
            ?? throw new InvalidOperationException("Durable WTT cleanup storage is required.");
        var saved = await store.GetAsync(WttPreparationIdentity.Id(binding), ct)
            ?? throw new InvalidDataException("Cleanup signed candidate is not durably admitted.");
        byte[] Compile(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> keys)
            => CompileWttCleanupBound(recovery, keys, quote, binding, plan);
        var exact = await ReadWttSolanaWireAsync(saved, binding, WttCleanupReservation(binding, quote), [plan.PayerBinding], Compile, ct);
        if (exact != prepared) throw new InvalidDataException("Cleanup differs from the exact persisted candidate.");
        return ReadWttSolanaPlan(saved, binding, [plan.PayerBinding], Compile);
    }
}
