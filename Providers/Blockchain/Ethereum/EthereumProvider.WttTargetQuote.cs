using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IWttTargetModule
{
    private readonly WttProofVerifier? _wttProofVerifier;
    private sealed record WttEvmTargetPlan(int Version, string IntentHash, string Sender, string ProtocolIdentity,
        string TransferBodyDigest, string? AttestationBodyDigest, string PriorityFee, string MaxFee,
        WttEvmDeployment Deployment, string? AssetAddress, byte AssetDecimals, WttEvmAssetPin AssetPin,
        string AssetBehaviorIdentity, IReadOnlyList<WttEvmStep> Steps);

    /// <inheritdoc cref="IWttTargetModule.QuoteWttTargetAsync"/>
    public async Task<AZOAResult<WttStageQuote>> QuoteWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs,
        CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        if (!ValidWttTargetIntent(intent) || proofs?.Transfer is null)
            return Error<WttStageQuote>("Exact target payer, recipient, proof and target authority are required.");
        try
        {
            var transfer = WttProofBinding.Transfer(intent, proofs.Transfer);
            await VerifyWttTargetProofAsync(intent, transfer, ct);
            var inspection = await InspectWttTargetAsync(intent.Asset, intent.RecipientAddress, intent.UnwrapNative, ct);
            if (inspection.IsError || inspection.Result is null) return Error<WttStageQuote>(inspection.Message);
            var target = inspection.Result;
            var snapshot = await ReadWttSnapshotAsync(ct);
            if (target.Network != intent.Target || WttDescriptor(snapshot) != intent.Target
                || target.Decimals != intent.TargetDecimals
                || intent.TargetAssetAddress is not null && !EthereumAddress.AreSame(intent.TargetAssetAddress, target.AssetAddress))
                return Error<WttStageQuote>("Target deployment or representation differs from the frozen intent.");
            if (await ReadWttUIntAsync(snapshot.Deployment.TokenBridge, "isTransferCompleted(bytes32)",
                    [Convert.FromHexString(transfer.BodyDigest)], snapshot.Block, ct) != 0)
                return Error<WttStageQuote>("This canonical WTT body is already consumed; reconcile its owned operation.");
            WttVaa? attestation = null;
            if (!target.Registered)
            {
                if (proofs.Attestation is null) return Error<WttStageQuote>("The original asset's verified metadata attestation is required for registration.");
                attestation = WttProofBinding.Attestation(intent, proofs.Attestation);
                await VerifyWttTargetProofAsync(intent, attestation, ct);
            }
            var fees = await GetFeeDataAsync(ct);
            if (fees.IsError || fees.Result is null) return Error<WttStageQuote>(fees.Message);
            var steps = new List<WttEvmStep>();
            void Add(string stage, WttVaa proof, bool registration)
            {
                var configured = _config[$"AZOA:Ethereum:Wtt:Networks:{ActiveNetwork}:StageGasLimits:{stage}"];
                if (!BigInteger.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var gas)
                    || gas <= 0 || gas > _maxGasLimit || gas.ToString(CultureInfo.InvariantCulture) != configured)
                    throw new InvalidDataException("An explicit bounded gas policy is required for WTT stage " + stage + ".");
                var reservation = new WttStageReservation(stage, (gas * fees.Result.MaxFeePerGas).ToString(CultureInfo.InvariantCulture), "0", "0");
                steps.Add(new(stage, snapshot.Deployment.TokenBridge, "0",
                    WttEthereumProtocol.VaaCall(Convert.FromHexString(proof.EnvelopeHex), registration, !registration && intent.UnwrapNative),
                    gas.ToString(CultureInfo.InvariantCulture), reservation));
            }
            if (attestation is not null) Add("Register", attestation, true);
            Add("Redeem", transfer, false);
            _ = WttPayloadCodec.ParseAmount(intent.TargetBudget.MaximumLockedDeposit);
            _ = WttPayloadCodec.ParseAmount(intent.TargetBudget.MaximumValue);
            if (steps.Aggregate(BigInteger.Zero, (sum, step) => sum + WttPayloadCodec.ParseAmount(step.Reservation.NetworkFee))
                > WttPayloadCodec.ParseAmount(intent.TargetBudget.MaximumNetworkFee))
                return Error<WttStageQuote>("The complete registration and redemption plan exceeds the authorized target cost.");
            var hash = CanonicalJson.Hash(intent);
            var targetAsset = target.Registered
                ? await ReadWttAssetAsync(snapshot, target.AssetAddress!, false, ct)
                : new WttAssetDescriptor("", false, target.Decimals, intent.Asset.OriginChain, intent.Asset.OriginAddress,
                    intent.Asset.OriginDecimals, "", "", "");
            var assetPin = await ReadWttAssetPinAsync(snapshot, targetAsset, ct);
            var behavior = WttAssetBehaviorIdentity(targetAsset, snapshot.Deployment, assetPin);
            if (!target.Registered) await RequireWttCodeAsync(assetPin.ImplementationAddress!, assetPin.ImplementationHash, snapshot.Block, ct);
            if (target.Registered && targetAsset.BehaviorIdentity != behavior)
                return Error<WttStageQuote>("Target token code policy changed while constructing the quote.");
            var plan = new WttEvmTargetPlan(1, hash, intent.TargetAuthority.Address, snapshot.ProtocolIdentity,
                transfer.BodyDigest, attestation?.BodyDigest, fees.Result.MaxPriorityFeePerGas.ToString(CultureInfo.InvariantCulture),
                fees.Result.MaxFeePerGas.ToString(CultureInfo.InvariantCulture), snapshot.Deployment, target.AssetAddress, target.Decimals,
                assetPin, behavior, steps);
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
            var expires = DateTimeOffset.UtcNow.AddSeconds(30);
            return Ok(new WttStageQuote(CanonicalJson.Hash(new { Version = 1, IntentHash = hash, Plan = json, ExpiresAt = expires }),
                hash, snapshot.ProtocolIdentity, "Target", steps.Select(step => step.Reservation).ToArray(), json, expires));
        }
        catch (FormatException exception) { return Error<WttStageQuote>(exception.Message); }
        catch (InvalidDataException exception) { return Error<WttStageQuote>(exception.Message); }
    }

    private async Task VerifyWttTargetProofAsync(WttOperationIntent intent, WttVaa vaa, CancellationToken ct)
    {
        using var scope = _wttProofVerifier is null ? _custodyScopeFactory?.CreateScope() : null;
        var verifier = _wttProofVerifier ?? scope?.ServiceProvider.GetRequiredService<WttProofVerifier>()
            ?? throw new InvalidOperationException("WTT target proof verification is unavailable.");
        var result = await verifier.VerifyAsync(Convert.ToBase64String(Convert.FromHexString(vaa.EnvelopeHex)),
            new(intent.Target.ProtocolIdentity, intent.Target.CoreBridge, intent.Target.TokenBridge, vaa.BodyHex), this, ct);
        if (result.IsError || result.Result is null) throw new InvalidDataException(result.Message);
    }

    private bool ValidWttTargetIntent(WttOperationIntent intent)
        => intent is { Version: 1, Source: not null, Target: not null, Asset: not null, Amount: not null,
                TargetAuthority: not null, TargetBudget: not null }
            && intent.ActorUserId != Guid.Empty && !string.IsNullOrWhiteSpace(intent.OperationId)
            && intent.Target.ChainType == ChainType && intent.Target.Network == ActiveNetwork
            && intent.Source.DeploymentIdentity is not null && intent.Target.DeploymentIdentity is not null
            && intent.Source.ProtocolChain != 0 && intent.Source.ProtocolChain != intent.Target.ProtocolChain
            && intent.Source.DeploymentIdentity.ProtocolChain == intent.Source.ProtocolChain
            && intent.Target.DeploymentIdentity.ProtocolChain == intent.Target.ProtocolChain
            && EthereumAddress.IsNonZero(intent.TargetAuthority.Address) && EthereumAddress.IsNonZero(intent.RecipientAddress)
            && intent.RecipientProtocolAddress == new string('0', 24) + intent.RecipientAddress[2..].ToLowerInvariant()
            && !intent.TargetAuthority.Context.IsPlatform && intent.TargetAuthority.Context.UserId == intent.ActorUserId
            && intent.TargetAuthority.Context.WalletId != Guid.Empty && intent.TargetAuthority.Context.Scope == AzoaScopes.WttTargetSign
            && intent.TargetAuthority.Context.ChainType == ChainType
            && intent.TargetAuthority.Context.ActingTenantId == (intent.TenantId ?? Guid.Empty)
            && (intent.TenantId is null || intent.TargetAuthority.Context.GrantorUserId == intent.ActorUserId);
}
