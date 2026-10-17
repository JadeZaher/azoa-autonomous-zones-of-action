using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    private async Task<(ulong Round, ulong Asset)> RequireWttAlgoTargetReadinessAsync(WttOperationIntent intent,
        WttAlgorandTargetPlan plan, WttAlgorandTargetStep step, CancellationToken ct)
    {
        var acceptedProof = step.Purpose == "Attestation" ? plan.Attestation! : plan.Transfer;
        var proof = await ReadWttAlgoTargetProofAsync(intent, acceptedProof, step.Purpose, ct);
        if (proof.Consumed) throw new InvalidDataException("Target body is already consumed; reconcile its original operation.");
        if (!proof.Proof.GuardianEvidence.GuardianAddresses.SequenceEqual(acceptedProof.GuardianEvidence.GuardianAddresses))
            throw new InvalidDataException("Exact verification group guardian membership changed from its accepted plan.");
        var snapshot = proof.Snapshot;
        var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
        if (WttAlgoUnsigned(parameters, "last-round") != snapshot.Round
            || WttAlgoText(parameters, "genesis-hash") != plan.GenesisHash || WttAlgoText(parameters, "genesis-id") != plan.GenesisId
            || WttAlgoUnsigned(parameters, "min-fee") != plan.MinimumFee
            || CanonicalJson.Hash(PriceWttAlgoTargetPlan(intent, plan, WttAlgoUnsigned(parameters, "fee"), snapshot.Round)) != CanonicalJson.Hash(plan))
            throw new InvalidDataException("Target network or exact transaction fee requirements changed.");
        var inspected = await InspectWttTargetAsync(intent.Asset, intent.RecipientAddress, intent.UnwrapNative, ct);
        if (inspected.IsError || inspected.Result is not { } target || target.Network != intent.Target || target.Decimals != plan.Decimals)
            throw new InvalidDataException("Target representation is unavailable or differs from its accepted identity.");
        var asset = target.AssetAddress is null ? (ulong?)null : UInt64Text.Parse(target.AssetAddress);
        if (plan.Asset is { } accepted && asset != accepted)
            throw new InvalidDataException("Previously registered target representation changed.");
        if (step.Stage == "Register" && target.Registered)
            throw new InvalidDataException("Registration now exists; reconcile the original attestation before proceeding.");
        if (step.Stage is "Redeem" or "RecipientOptIn" && (!target.Registered || asset is null))
            throw new InvalidDataException("Target registration must be authoritatively completed before this stage.");
        if (step.Stage is "Register" or "Redeem")
        {
            if (!proof.SequenceExists) throw new InvalidDataException("The exact target sequence account must be initialized first.");
            var custody = await ReadWttAlgorandJsonAsync("/v2/accounts/" + plan.CustodyAddress, ct);
            var blob = WttAlgorandStorage.Read(custody, plan.TokenApplication, plan.CustodyAddress, snapshot.Round);
            if (blob is null || step.Stage == "Register" && (blob.Metadata.Length != 0 || blob.Bytes.Any(value => value != 0)))
                throw new InvalidDataException("Target custody storage has not completed its required initialization.");
            if (step.Stage == "Redeem" && !target.RecipientReady)
                throw new InvalidDataException("The exact recipient prerequisite has not completed.");
        }
        if (step.StorageApplication != 0)
        {
            var address = WttAlgorandStorage.Address(step.StorageApplication, step.StorageIndex, Convert.FromHexString(step.StorageEmitterHex));
            var existing = await ReadWttAlgorandJsonAsync("/v2/accounts/" + address, ct, allowMissing: true);
            if (existing.ValueKind != JsonValueKind.Undefined && !WttAlgorandStorage.IsUnallocated(existing, address, snapshot.Round))
                throw new InvalidDataException("Storage already exists or changed authority; reconcile the original prerequisite.");
        }
        var signer = step.Stage == "RecipientOptIn" ? intent.RecipientAddress : intent.TargetAuthority.Address;
        var payer = await ReadWttAlgorandJsonAsync("/v2/accounts/" + signer, ct);
        if (WttAlgoText(payer, "address") != signer || WttAlgoUnsigned(payer, "round") != snapshot.Round
            || payer.TryGetProperty("auth-addr", out var rekey) && (rekey.ValueKind != JsonValueKind.String || rekey.GetString() != signer))
            throw new InvalidDataException("The exact stage participant has incompatible current signing authority.");
        if (step.Stage == "RecipientOptIn")
        {
            if (asset is null or 0 || target.RecipientReady)
                throw new InvalidDataException("Recipient opt-in no longer matches an absent non-native holding.");
            if (payer.TryGetProperty("assets", out var holdings)
                && (holdings.ValueKind != JsonValueKind.Array || holdings.EnumerateArray().Any(item => WttAlgoUnsigned(item, "asset-id") == asset.Value)))
                throw new InvalidDataException("An existing or malformed recipient holding cannot be repaired by opt-in.");
        }
        var required = checked(UInt64Text.Parse(step.Reservation.NetworkFee) + UInt64Text.Parse(step.Reservation.LockedDeposit)
            + UInt64Text.Parse(step.Reservation.Value) + WttAlgoUnsigned(payer, "min-balance"));
        if (WttAlgoUnsigned(payer, "amount") < required)
            throw new InvalidDataException("The exact stage participant must fund its accepted fee and deposit.");
        await RequireWttAlgoRoundAsync(snapshot.Round, ct);
        return (snapshot.Round, asset ?? 0);
    }
}
