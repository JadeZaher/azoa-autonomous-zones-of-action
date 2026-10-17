using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IWttSourceModule
{
    /// <inheritdoc/>
    public async Task<AZOAResult<WttPublicationRules>> ReadWttPublicationRulesAsync(CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        try
        {
            var snapshot = await ReadWttSnapshotAsync(ct);
            var finality = await ReadWttUIntAsync(snapshot.Deployment.TokenBridge, "finality()", [], snapshot.Block, ct);
            return finality < 0 || finality > byte.MaxValue
                ? Error<WttPublicationRules>("The bridge's actual publication finality is outside the protocol byte range.")
                : Ok(new WttPublicationRules((byte)finality, false));
        }
        catch (FormatException exception) { return Error<WttPublicationRules>(exception.Message); }
        catch (InvalidDataException exception) { return Error<WttPublicationRules>(exception.Message); }
    }

    private sealed record WttEvmStep(string Stage, string Target, string Value, string Data, string GasLimit,
        WttStageReservation Reservation);
    private sealed record WttEvmPlan(int Version, string IntentHash, string Sender, string ProtocolIdentity,
        string PriorityFee, string MaxFee, string MessageFee, WttEvmDeployment Deployment, WttEvmAssetPin AssetPin,
        IReadOnlyList<WttEvmStep> Steps);
    private sealed record WttEvmAssetPin(string Kind, string RuntimeHash, string ImplementationHash, string? ImplementationAddress);

    /// <inheritdoc cref="IWttSourceModule.QuoteWttSourceAsync"/>
    public async Task<AZOAResult<WttStageQuote>> QuoteWttSourceAsync(WttOperationIntent intent, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        if (!ValidWttSourceIntent(intent)) return Error<WttStageQuote>("Exact actor, network, asset and WTT source authority are required.");
        try
        {
            var snapshot = await ReadWttSnapshotAsync(ct);
            if (WttDescriptor(snapshot) != intent.Source)
                return Error<WttStageQuote>("Source deployment changed from the frozen WTT intent.");
            var asset = await ReadWttAssetAsync(snapshot, intent.Asset.TokenAddress, intent.Asset.NativeAsset, ct);
            if (asset != intent.Asset) return Error<WttStageQuote>("Source asset identity or behavior changed.");
            var finality = await ReadWttUIntAsync(snapshot.Deployment.TokenBridge, "finality()", [], snapshot.Block, ct);
            if (finality != intent.ConsistencyLevel) return Error<WttStageQuote>("The bridge's actual publication finality differs from the frozen intent.");
            var normalized = WttPayloadCodec.Normalize(intent.Amount.Requested,
                DenormalizedWttFee(intent).ToString(CultureInfo.InvariantCulture), asset.Decimals);
            if (normalized != intent.Amount) return Error<WttStageQuote>("WTT amount, dust or fee differs from exact protocol normalization.");
            if (intent.RequiresAttestation && asset.OriginChain != snapshot.ProtocolChain)
                return Error<WttStageQuote>("A wrapped representation cannot attest as its original asset; obtain the origin-chain attestation.");
            var fees = await GetFeeDataAsync(ct);
            if (fees.IsError || fees.Result is null) return Error<WttStageQuote>(fees.Message);
            var messageFee = await ReadWttUIntAsync(snapshot.Deployment.Core, "messageFee()", [], snapshot.Block, ct);
            var amount = WttPayloadCodec.ParseAmount(intent.Amount.Debited);
            var steps = new List<WttEvmStep>();
            void Add(string stage, string target, BigInteger value, string data)
            {
                var configured = _config[$"AZOA:Ethereum:Wtt:Networks:{ActiveNetwork}:StageGasLimits:{stage}"];
                if (!BigInteger.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var gas)
                    || gas <= 0 || gas > _maxGasLimit || gas.ToString(CultureInfo.InvariantCulture) != configured)
                    throw new InvalidDataException("An explicit bounded gas policy is required for WTT stage " + stage + ".");
                var reservation = new WttStageReservation(stage, (gas * fees.Result.MaxFeePerGas).ToString(CultureInfo.InvariantCulture),
                    "0", value.ToString(CultureInfo.InvariantCulture));
                steps.Add(new(stage, target, value.ToString(CultureInfo.InvariantCulture), data,
                    gas.ToString(CultureInfo.InvariantCulture), reservation));
            }
            if (!asset.NativeAsset)
            {
                var allowance = await ReadWttUIntAsync(asset.TokenAddress, "allowance(address,address)",
                    [intent.SourceAuthority.Address, snapshot.Deployment.TokenBridge], snapshot.Block, ct);
                if (allowance < amount)
                {
                    if (allowance != 0) Add("ResetAllowance", asset.TokenAddress, 0,
                        EthereumAbi.EncodeCall("approve(address,uint256)", snapshot.Deployment.TokenBridge, BigInteger.Zero));
                    Add("Approve", asset.TokenAddress, 0,
                        EthereumAbi.EncodeCall("approve(address,uint256)", snapshot.Deployment.TokenBridge, amount));
                }
            }
            if (intent.RequiresAttestation) Add("Attest", snapshot.Deployment.TokenBridge, messageFee,
                WttEthereumProtocol.AttestationCall(asset.TokenAddress, intent.Nonce));
            Add("Transfer", snapshot.Deployment.TokenBridge, messageFee + (asset.NativeAsset ? amount : BigInteger.Zero),
                WttEthereumProtocol.TransferCall(asset.TokenAddress, amount, intent.Target.ProtocolChain,
                    WttPayloadCodec.FixedHex(intent.RecipientProtocolAddress, 32), DenormalizedWttFee(intent), intent.Nonce, asset.NativeAsset));
            var maximumFee = WttPayloadCodec.ParseAmount(intent.SourceBudget.MaximumNetworkFee);
            var maximumValue = WttPayloadCodec.ParseAmount(intent.SourceBudget.MaximumValue);
            _ = WttPayloadCodec.ParseAmount(intent.SourceBudget.MaximumLockedDeposit);
            if (steps.Aggregate(BigInteger.Zero, (total, step) => total + WttPayloadCodec.ParseAmount(step.Reservation.NetworkFee)) > maximumFee
                || steps.Aggregate(BigInteger.Zero, (total, step) => total + WttPayloadCodec.ParseAmount(step.Value)) > maximumValue)
                return Error<WttStageQuote>("The complete WTT source plan exceeds the explicitly authorized native costs.");
            var hash = CanonicalJson.Hash(intent);
            var assetPin = await ReadWttAssetPinAsync(snapshot, asset, ct);
            if (WttAssetBehaviorIdentity(asset, snapshot.Deployment, assetPin) != asset.BehaviorIdentity)
                return Error<WttStageQuote>("Source token code policy changed while constructing the quote.");
            var plan = new WttEvmPlan(1, hash, intent.SourceAuthority.Address, snapshot.ProtocolIdentity,
                fees.Result.MaxPriorityFeePerGas.ToString(CultureInfo.InvariantCulture), fees.Result.MaxFeePerGas.ToString(CultureInfo.InvariantCulture),
                messageFee.ToString(CultureInfo.InvariantCulture), snapshot.Deployment, assetPin, steps);
            var json = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan));
            var expires = DateTimeOffset.UtcNow.AddSeconds(30);
            var quoteId = CanonicalJson.Hash(new { Version = 1, IntentHash = hash, Plan = json, ExpiresAt = expires });
            return Ok(new WttStageQuote(quoteId, hash, snapshot.ProtocolIdentity, "Source", steps.Select(step => step.Reservation).ToArray(), json, expires));
        }
        catch (FormatException exception) { return Error<WttStageQuote>(exception.Message); }
        catch (InvalidDataException exception) { return Error<WttStageQuote>(exception.Message); }
    }

    private static BigInteger DenormalizedWttFee(WttOperationIntent intent)
        => WttPayloadCodec.ParseAmount(intent.Amount.Fee) * BigInteger.Pow(10, Math.Max(0, intent.Asset.Decimals - 8));

    private bool ValidWttSourceIntent(WttOperationIntent intent)
        => intent is { Version: 1, Source: not null, Target: not null, Asset: not null, Amount: not null,
                SourceAuthority: not null, SourceBudget: not null }
            && !string.IsNullOrWhiteSpace(intent.OperationId) && intent.ActorUserId != Guid.Empty
            && intent.Source.Network == ActiveNetwork && intent.Source.ChainType == "Ethereum"
            && intent.Source.DeploymentIdentity is not null && intent.Target.DeploymentIdentity is not null
            && intent.Source.DeploymentIdentity.ProtocolChain == intent.Source.ProtocolChain
            && intent.Target.DeploymentIdentity.ProtocolChain == intent.Target.ProtocolChain
            && intent.Target.ProtocolChain != 0 && intent.Target.ProtocolChain != intent.Source.ProtocolChain
            && EthereumAddress.IsNonZero(intent.SourceAuthority.Address)
            && intent.SourceAuthority.Context.IsResolvableUserContext && intent.SourceAuthority.Context.UserId == intent.ActorUserId
            && intent.SourceAuthority.Context.ActingTenantId == (intent.TenantId ?? Guid.Empty)
            && (intent.TenantId is null || intent.SourceAuthority.Context.GrantorUserId == intent.ActorUserId)
            && intent.SourceAuthority.Context.Scope == AzoaScopes.WttSourceSign
            && intent.SourceAuthority.Context.ChainType == "Ethereum";
}
