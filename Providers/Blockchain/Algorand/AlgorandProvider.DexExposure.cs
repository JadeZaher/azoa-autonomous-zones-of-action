using System.Globalization;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IDexTransactionModule.GetExposedSwapTransactionIds"/>
    public AZOAResult<IReadOnlyList<string>> GetExposedSwapTransactionIds(SwapIntent intent, SwapStagePlan plan)
    {
        var wire = ReadTinymanPlan(intent, plan);
        return AZOAResult<IReadOnlyList<string>>.Success(TinymanSwapWire.Build(wire).Select(transaction => transaction.TxID()).ToArray());
    }

    /// <inheritdoc cref="IDexTransactionModule.ObserveExposedSwapStageAsync"/>
    public async Task<AZOAResult<SwapExternalStageObservation>> ObserveExposedSwapStageAsync(SwapIntent intent,
        SwapStagePlan plan, CancellationToken ct = default, SwapStageObservation? previous = null)
    {
        var wire = ReadTinymanPlan(intent, plan);
        var transactions = TinymanSwapWire.Build(wire);
        var ids = transactions.Select(transaction => transaction.TxID()).ToArray();
        var result = await ObserveTinymanPlanAsync(intent, plan, ids, ct);
        if (result.IsError || result.Result is not { } observation)
            return AZOAResult<SwapExternalStageObservation>.Failure(result.Message);
        if (observation.Verdict != "Confirmed")
            return AZOAResult<SwapExternalStageObservation>.Success(new(observation));
        var envelopes = new List<string>();
        for (var index = 0; index < ids.Length; index++)
        {
            var indexed = await ReadTinymanIndexedTransactionAsync(ids[index], ct);
            if (indexed?.Transaction is not { } actual || !actual.TryGetProperty("signature", out var signature) ||
                signature.ValueKind != JsonValueKind.Object || signature.EnumerateObject().Any(property => property.Name != "sig" &&
                    (property.Name is not ("multisig" or "logicsig" or "pqsig") || property.Value.ValueKind != JsonValueKind.Null)) ||
                !signature.TryGetProperty("sig", out var encoded) || encoded.ValueKind != JsonValueKind.String ||
                actual.TryGetProperty("auth-addr", out var authorization) && authorization.ValueKind != JsonValueKind.Null)
                return Pending();
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded.GetString()!); }
            catch (FormatException) { return Pending(); }
            if (bytes.Length != 64 || Convert.ToBase64String(bytes) != encoded.GetString()) return Pending();
            var signed = new SignedTransaction(transactions[index], bytes, null, null, null);
            try { AlgorandSignedEnvelope.RequireSingleSender(signed, wire.Owner); }
            catch (InvalidOperationException) { return Pending(); }
            envelopes.Add(Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(signed)));
        }
        var recovered = ValidateSignedSwapStage(intent, plan, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(envelopes)));
        if (recovered.IsError || recovered.Result is null || !recovered.Result.TransactionIds.SequenceEqual(ids)) return Pending();
        return AZOAResult<SwapExternalStageObservation>.Success(new(observation, recovered.Result));

        AZOAResult<SwapExternalStageObservation> Pending() => AZOAResult<SwapExternalStageObservation>.Success(new(
            new("Pending", ids, intent.Terms.Network.ProtocolIdentity,
                Detail: "Finalized group exists but its actual original sender signatures are not yet recoverable.")));
    }

    /// <inheritdoc cref="IDexTransactionModule.ValidateExposedSwapNoEffect"/>
    public bool ValidateExposedSwapNoEffect(SwapIntent intent, SwapStagePlan plan, SwapStageObservation observation)
    {
        if (observation.Verdict != "ExpiredAbsent" || observation.ProtocolIdentity != intent.Terms.Network.ProtocolIdentity ||
            observation.ActualInput != "0" || observation.ActualOutput != "0" || observation.PlatformFeePaid != "0" ||
            observation.NetworkFeePaid != "0" || observation.LockedBalance != "0" || observation.EvidenceJson is null) return false;
        try
        {
            var wire = ReadTinymanPlan(intent, plan);
            var ids = TinymanSwapWire.Build(wire).Select(transaction => transaction.TxID()).ToArray();
            using var document = JsonDocument.Parse(observation.EvidenceJson);
            var proof = document.RootElement;
            CanonicalJson.Canonicalize(proof);
            return observation.TransactionIds.SequenceEqual(ids) && proof.ValueKind == JsonValueKind.Object &&
                proof.EnumerateObject().Count() == 6 &&
                proof.GetProperty("format").GetString() == "algorand-complete-group-absence/1" &&
                proof.GetProperty("genesisHash").GetString() == wire.GenesisHash &&
                proof.GetProperty("planHash").GetString() == CanonicalJson.Hash(plan) &&
                proof.GetProperty("lastValid").GetString() == wire.LastValid.ToString(CultureInfo.InvariantCulture) &&
                proof.GetProperty("transactionIds").EnumerateArray().Select(value => value.GetString()).SequenceEqual(ids) &&
                proof.GetProperty("expiryBlockDigest").GetString() is { Length: 64 } digest && digest.All(char.IsAsciiHexDigitLower);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
