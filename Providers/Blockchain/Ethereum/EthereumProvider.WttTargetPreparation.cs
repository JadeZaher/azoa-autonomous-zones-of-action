using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider
{
    /// <inheritdoc cref="IWttTargetModule.PrepareWttTargetAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        WttStageQuote quote, string stage, int generation, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        if (generation < 0) return Error<WttStagePreparation>("WTT generation cannot be negative.");
        try
        {
            var plan = ReadWttTargetPlan(intent, proofs, quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == stage)
                ?? throw new InvalidDataException("Target stage is absent from its admitted plan.");
            var saved = await ReadSavedWttPreparationAsync(intent, quote, step, plan.Sender, intent.TargetAuthority.Context,
                "Target", generation, intent.Target.DeploymentIdentity.GenesisHex, ct);
            if (saved is not null) return Ok(saved);
            var transfer = WttProofBinding.Transfer(intent, proofs.Transfer);
            await VerifyWttTargetProofAsync(intent, transfer, ct);
            if (stage == "Register") await VerifyWttTargetProofAsync(intent, WttProofBinding.Attestation(intent, proofs.Attestation!), ct);
            var snapshot = await ReadWttSnapshotAsync(ct);
            if (WttDescriptor(snapshot) != intent.Target || snapshot.Deployment != plan.Deployment)
                return Error<WttStagePreparation>("Target deployment changed before preparation.");
            var inspection = await InspectWttTargetAsync(intent.Asset, intent.RecipientAddress, intent.UnwrapNative, ct);
            if (inspection.IsError || inspection.Result is null) return Error<WttStagePreparation>(inspection.Message);
            if (inspection.Result.Network != intent.Target || inspection.Result.Decimals != plan.AssetDecimals
                || plan.AssetAddress is not null && !EthereumAddress.AreSame(plan.AssetAddress, inspection.Result.AssetAddress)
                || stage == "Redeem" && !inspection.Result.Registered)
                return Error<WttStagePreparation>("Target representation is not ready for the exact admitted stage.");
            var asset = inspection.Result.Registered
                ? await ReadWttAssetAsync(snapshot, inspection.Result.AssetAddress!, false, ct)
                : new WttAssetDescriptor("", false, plan.AssetDecimals, intent.Asset.OriginChain, intent.Asset.OriginAddress,
                    intent.Asset.OriginDecimals, "", "", "");
            if (await ReadWttAssetPinAsync(snapshot, asset, ct) != plan.AssetPin
                || WttAssetBehaviorIdentity(asset, snapshot.Deployment, plan.AssetPin) != plan.AssetBehaviorIdentity
                || inspection.Result.Registered && asset.BehaviorIdentity != plan.AssetBehaviorIdentity)
                return Error<WttStagePreparation>("Target token code or wrapped implementation changed before preparation.");
            if (!inspection.Result.Registered)
                await RequireWttCodeAsync(plan.AssetPin.ImplementationAddress!, plan.AssetPin.ImplementationHash, snapshot.Block, ct);
            var chain = await GetChainIdAsync(ct);
            if (chain.IsError) return Error<WttStagePreparation>(chain.Message);
            var protocol = "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture);
            var key = WttPreparationKey(intent.OperationId, "Target", stage, generation);
            var prepared = await PrepareCallAsync(key, plan.Sender, step.Target, BigInteger.Zero, Convert.FromHexString(step.Data[2..]),
                "WTT " + stage, intent.TargetAuthority.Context, ct, WttOwnerIntent(intent, quote),
                new EthereumFeeAuthorization(WttPayloadCodec.ParseAmount(step.GasLimit), WttPayloadCodec.ParseAmount(plan.PriorityFee),
                    WttPayloadCodec.ParseAmount(plan.MaxFee), WttPayloadCodec.ParseAmount(step.Reservation.NetworkFee), BigInteger.Zero));
            if (prepared.IsError || prepared.Result is null) return Error<WttStagePreparation>(prepared.Message);
            var row = prepared.Result;
            if (row.Id != EthereumIdentity(protocol, plan.Sender.ToLowerInvariant(), key)
                || row.TransactionId is null || row.SignedTransactionBase64 is null)
                throw new InvalidOperationException("Target preparation did not retain its exact nonce and signed candidate.");
            return Ok(new WttStagePreparation(stage, generation, row.TransactionId, row.SignedTransactionBase64, plan.ProtocolIdentity,
                CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttEvmRecovery(row.Id, protocol, plan.IntentHash,
                    quote.QuoteId, stage, generation))), step.Reservation));
        }
        catch (JsonException) { return Error<WttStagePreparation>("Malformed WTT target plan."); }
        catch (FormatException exception) { return Error<WttStagePreparation>(exception.Message); }
        catch (InvalidDataException exception) { return Error<WttStagePreparation>(exception.Message); }
    }

    /// <inheritdoc cref="IWttTargetModule.SubmitWttTargetAsync"/>
    public async Task SubmitWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        var plan = ReadWttTargetPlan(intent, proofs, quote);
        var step = plan.Steps.SingleOrDefault(value => value.Stage == prepared.Stage)
            ?? throw new InvalidDataException("Prepared stage is absent from the exact target plan.");
        var row = await ReadWttPreparationAsync(intent, quote, prepared, step, plan.Sender, intent.TargetAuthority.Context, "Target", ct);
        await RequireWttOriginalGenesisAsync(intent.Target.DeploymentIdentity.GenesisHex, ct);
        await SubmitPreparationAsync(row, ct);
    }

    private WttEvmTargetPlan ReadWttTargetPlan(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote)
    {
        if (!ValidWttTargetIntent(intent) || quote is null || quote.Stages is null || quote.Phase != "Target"
            || quote.ProtocolIdentity != intent.Target.ProtocolIdentity || quote.IntentHash != CanonicalJson.Hash(intent))
            throw new InvalidDataException("Target quote does not bind the exact WTT intent.");
        var transfer = WttProofBinding.Transfer(intent, proofs.Transfer);
        var plan = JsonSerializer.Deserialize<WttEvmTargetPlan>(quote.PlanJson);
        if (plan is not { Version: 1, Steps: not null, Deployment: not null, AssetPin: not null } || plan.Steps.Count is < 1 or > 2
            || plan.Steps.Any(step => step is null || step.Reservation is null) || plan.IntentHash != quote.IntentHash
            || plan.Sender != intent.TargetAuthority.Address || plan.ProtocolIdentity != quote.ProtocolIdentity
            || CanonicalJson.Hash(plan.Deployment) != intent.Target.CodeIdentity || plan.TransferBodyDigest != transfer.BodyDigest
            || plan.AssetDecimals != intent.TargetDecimals
            || quote.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan))
            || quote.QuoteId != CanonicalJson.Hash(new { Version = 1, IntentHash = plan.IntentHash, Plan = quote.PlanJson, ExpiresAt = quote.ExpiresAt })
            || !plan.Steps.Select(step => step.Reservation).SequenceEqual(quote.Stages))
            throw new InvalidDataException("WTT target plan or canonical body identity changed.");
        var targetAsset = new WttAssetDescriptor(plan.AssetAddress ?? "", false, plan.AssetDecimals, intent.Asset.OriginChain,
            intent.Asset.OriginAddress, intent.Asset.OriginDecimals, "", "", "");
        if (WttAssetBehaviorIdentity(targetAsset, plan.Deployment, plan.AssetPin) != plan.AssetBehaviorIdentity)
            throw new InvalidDataException("Target token behavior evidence differs from its frozen plan.");
        WttVaa? attestation = null;
        if (plan.Steps.Count == 2)
        {
            if (proofs.Attestation is null || plan.AssetAddress is not null)
                throw new InvalidDataException("Registration requires the original attestation and absent representation.");
            attestation = WttProofBinding.Attestation(intent, proofs.Attestation);
        }
        if (plan.AttestationBodyDigest != attestation?.BodyDigest
            || attestation is null && !EthereumAddress.IsNonZero(plan.AssetAddress)
            || intent.TargetAssetAddress is not null && !EthereumAddress.AreSame(intent.TargetAssetAddress, plan.AssetAddress)
            || !plan.Steps.Select(step => step.Stage).SequenceEqual(attestation is null ? new[] { "Redeem" } : new[] { "Register", "Redeem" }))
            throw new InvalidDataException("WTT target prerequisite ordering changed.");
        var maxFee = WttPayloadCodec.ParseAmount(plan.MaxFee);
        if (maxFee < WttPayloadCodec.ParseAmount(plan.PriorityFee)) throw new InvalidDataException("Invalid target fee authorization.");
        BigInteger totalFee = 0;
        foreach (var step in plan.Steps)
        {
            var register = step.Stage == "Register";
            var data = WttEthereumProtocol.VaaCall(Convert.FromHexString((register ? attestation! : transfer).EnvelopeHex),
                register, !register && intent.UnwrapNative);
            var gas = WttPayloadCodec.ParseAmount(step.GasLimit);
            if (gas <= 0 || step.Target != intent.Target.TokenBridge || step.Value != "0" || step.Data != data
                || step.Reservation != new WttStageReservation(step.Stage, (gas * maxFee).ToString(CultureInfo.InvariantCulture), "0", "0"))
                throw new InvalidDataException("Target call bytes or reservation differ from their authorization.");
            totalFee += gas * maxFee;
        }
        if (totalFee > WttPayloadCodec.ParseAmount(intent.TargetBudget.MaximumNetworkFee))
            throw new InvalidDataException("Target plan exceeds the caller's authorized costs.");
        return plan;
    }
}
