using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttNativeOutputModule.SubmitWttNativeOutputAsync"/>
    public async Task SubmitWttNativeOutputAsync(WttOperationIntent intent, WttNativeOutputQuote quote,
        WttNativeOutputConsent consent, WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (_, _, wire) = await ReadWttNativeOutputPreparationAsync(intent, quote, consent, prepared, ct);
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.GetInt64() > wire.Recovery.LastValidBlockHeight) return;
        if (await NativeMessageFeeAsync(Convert.FromBase64String(wire.MessageBase64), ct) > UInt64Text.Parse(quote.NetworkFee))
            throw new InvalidDataException("Native-output network fee exceeds the accepted bound.");
        var sent = await DurableRpcAsync("sendTransaction", [prepared.SignedTransactionBase64,
            new { encoding = "base64", skipPreflight = false, preflightCommitment = "finalized", maxRetries = 0 }], ct);
        if (sent.ValueKind != JsonValueKind.String || sent.GetString() != prepared.TransactionId)
            throw new InvalidDataException("Native-output send response differs from the saved candidate; preserve its ambiguous outcome.");
    }

    /// <inheritdoc cref="IWttNativeOutputModule.ObserveWttNativeOutputAsync"/>
    public async Task<AZOAResult<WttNativeOutputObservation>> ObserveWttNativeOutputAsync(WttOperationIntent intent,
        WttNativeOutputQuote quote, WttNativeOutputConsent consent, WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (plan, binding, wire) = await ReadWttNativeOutputPreparationAsync(intent, quote, consent, prepared, ct);
        try
        {
            var transaction = await ReadWttFinalizedTransactionAsync(prepared.TransactionId, ct);
            if (transaction.ValueKind == JsonValueKind.Null)
            {
                var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
                return Ok(new WttNativeOutputObservation(new(height.GetInt64() > wire.Recovery.LastValidBlockHeight ? "NeedsOperator" : "Pending",
                    prepared.TransactionId, prepared.ProtocolIdentity, Detail: "Missing finalized native-output history does not prove absence."), null, null));
            }
            var finalized = await RequireSolanaFinalityAsync(transaction, prepared.TransactionId, prepared.SignedTransactionBase64,
                wire.MessageBase64, [], UInt64Text.Parse(quote.NetworkFee), ct);
            if (finalized.Failed)
                return Ok(new WttNativeOutputObservation(new("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                    JsonSerializer.Serialize(new { finalized.BlockIdentity, noNativeOutput = true }),
                    NetworkFeePaid: finalized.Fee.ToString(CultureInfo.InvariantCulture)), "0", "0"));
            var temporary = WttSolanaNativeOutputWire.Build(intent, quote, WttPreparationIdentity.Id(binding), new(plan.Guard.Program),
                Convert.FromBase64String(plan.Guard.RentBase64), plan.ComputeUnits, plan.ComputePrice).Account;
            int Index(string address) => finalized.Accounts.Keys.Select((key, index) => (key, index)).Single(value => value.key.Key == address).index;
            var source = Index(quote.SourceTokenAccount); var recipient = Index(quote.RecipientAddress); var transient = Index(temporary.Address);
            var meta = finalized.Meta; var pre = meta.GetProperty("preBalances"); var post = meta.GetProperty("postBalances");
            var oldTokens = SolanaTokenBalanceEvidence.Read(meta.GetProperty("preTokenBalances"), source, quote.MintAddress, quote.RecipientAddress, 9, false);
            var newTokens = SolanaTokenBalanceEvidence.Read(meta.GetProperty("postTokenBalances"), source, quote.MintAddress, quote.RecipientAddress, 9, false);
            var amount = UInt64Text.Parse(quote.NativeAmount);
            if ((System.Numerics.BigInteger)oldTokens - newTokens != amount
                || (System.Numerics.BigInteger)pre[source].GetUInt64() - post[source].GetUInt64() != amount
                || (System.Numerics.BigInteger)post[recipient].GetUInt64() - pre[recipient].GetUInt64() + finalized.Fee != amount
                || pre[transient].GetUInt64() != 0 || post[transient].GetUInt64() != 0)
                throw new InvalidDataException("Finalized unwrap changed the original ATA, net native amount or transient rent return.");
            return Ok(new WttNativeOutputObservation(new("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity,
                JsonSerializer.Serialize(new { finalized.BlockIdentity, quote.RedemptionPreparationHash, quote.RedemptionObservationHash,
                    quote.RecipientAddress, quote.SourceTokenAccount, transientAccount = temporary.Address,
                    quote.NativeAmount, temporaryRentReturned = quote.TemporaryRent }),
                NetworkFeePaid: finalized.Fee.ToString(CultureInfo.InvariantCulture)), quote.NativeAmount, quote.TemporaryRent));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Ok(new WttNativeOutputObservation(new("NeedsOperator", prepared.TransactionId, prepared.ProtocolIdentity, Detail: exception.Message), null, null)); }
    }

    private async Task<(WttSolanaNativeOutputPlan Plan, WttPreparationBinding Binding, WttNativeOutputWirePlan Wire)>
        ReadWttNativeOutputPreparationAsync(WttOperationIntent intent, WttNativeOutputQuote quote, WttNativeOutputConsent consent,
            WttStagePreparation prepared, CancellationToken ct)
    {
        var plan = ReadWttNativeOutputQuote(intent, quote);
        var binding = WttNativeOutputBinding(intent, quote, consent, prepared.Generation);
        using var scope = _custodyScopeFactory?.CreateScope();
        var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
            ?? throw new InvalidOperationException("Durable native-output preparation storage is required.");
        var saved = await store.GetAsync(WttPreparationIdentity.Id(binding), ct)
            ?? throw new InvalidDataException("Native-output candidate was never durably admitted.");
        if (ReadWttNativeOutputSigned(intent, quote, plan, binding, saved) != prepared)
            throw new InvalidDataException("Native-output candidate differs from its durable exact signature.");
        var wire = ReadWttNativeOutputWire(intent, quote, plan, binding, saved);
        if (await VerifyGenesisAsync(ct) != wire.Recovery.Genesis) throw new InvalidDataException("Saved native output belongs to another genesis.");
        return (plan, binding, wire);
    }
}
