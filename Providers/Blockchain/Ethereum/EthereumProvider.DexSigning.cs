using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IDexTransactionModule.SignSwapStageAsync"/>
    public async Task<AZOAResult<PreparedSwapStage>> SignSwapStageAsync(SwapIntent intent, SwapStagePlan plan,
        IReadOnlyList<SwapSigningAuthority> authorities, CancellationToken ct = default)
    {
        var saved = ReadEthereumDexPlan(intent, plan);
        if (authorities is not { Count: 1 } || authorities[0].Participant != intent.Terms.Owner)
            return Error<PreparedSwapStage>("Exactly the accepted Ethereum owner must authorize custody.");
        var row = await WithPreparationStoreAsync(store => store.GetAsync(saved.ReservationId, ct));
        if (row is null || row.CallFingerprint != DexCallFingerprint(saved.Call) || row.IntentJson != JsonSerializer.Serialize(saved.Call) ||
            row.Nonce.TrimStart('0') != saved.Nonce.TrimStart('0'))
            return Error<PreparedSwapStage>("The exact durable Ethereum reservation is missing or changed.");
        var quote = ReadDexAcceptedIntent(intent);
        var snapshot = await ReadDexSnapshotAsync(quote.Deployment, ct);
        if (!CurrentDexTime(snapshot.ChainTime)) return Error<PreparedSwapStage>("Finalized Ethereum evidence is not current enough for fresh signing.");
        if (snapshot.ProtocolIdentity != saved.ProtocolIdentity)
            return Error<PreparedSwapStage>("The original physical network no longer permits fresh signing.");
        if (plan.Purpose == "Effect" && snapshot.ChainTime >= EthereumDexExecutionCodec.Amount(quote.ExecutionDeadline))
            return AZOAResult<PreparedSwapStage>.FailureWithCode("The executor authorization deadline elapsed; the stage will not be signed.",
                DexResultCodes.Unsettleable);
        var fees = await GetFeeDataAsync(ct);
        if (fees.IsError || fees.Result is null || fees.Result.MaxFeePerGas > EthereumDexExecutionCodec.Amount(saved.Call.MaxFee) ||
            fees.Result.MaxPriorityFeePerGas > EthereumDexExecutionCodec.Amount(saved.Call.PriorityFee))
            return Error<PreparedSwapStage>("Current Ethereum fees exceed the original stage reservation.");
        var signed = await SignReservedCallAsync(row, authorities[0].Context, ct);
        return signed.IsError || signed.Result?.SignedTransactionBase64 is null ? Error<PreparedSwapStage>(signed.Message)
            : ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(signed.Result.SignedTransactionBase64));
    }

    /// <inheritdoc cref="IDexTransactionModule.ValidateSignedSwapStage"/>
    public AZOAResult<PreparedSwapStage> ValidateSignedSwapStage(SwapIntent intent, SwapStagePlan plan, string signedPayloadJson)
    {
        try
        {
            var saved = ReadEthereumDexPlan(intent, plan);
            using var payload = JsonDocument.Parse(signedPayloadJson);
            if (payload.RootElement.ValueKind != JsonValueKind.String || payload.RootElement.GetString() is not { Length: <= 43692 } encoded)
                throw new InvalidDataException("Exactly one bounded Ethereum envelope is required.");
            var wire = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(wire) != encoded) throw new InvalidDataException("Ethereum wire base64 is not canonical.");
            var id = EthereumSignedEnvelope.Verify(wire, DexUnsigned(saved), intent.Terms.Owner.Address);
            return Ok(new PreparedSwapStage(plan, [id], CanonicalJson.Canonicalize(payload.RootElement)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or OverflowException or ArgumentException)
        { return Error<PreparedSwapStage>("Signed Ethereum bytes differ from the exact plan or original owner signature."); }
    }
}
