using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : IWttNonceReservationModule
{
    private sealed record WttEvmRecovery(string PreparationId, string AllocatorProtocol, string IntentHash,
        string QuoteId, string Stage, int Generation);

    /// <inheritdoc cref="IWttSourceModule.PrepareWttSourceAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        string stage, int generation, CancellationToken ct = default)
    {
        _ = RpcHttpClient;
        if (generation < 0) return Error<WttStagePreparation>("WTT generation cannot be negative.");
        try
        {
            var plan = ReadWttSourcePlan(intent, quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == stage)
                ?? throw new InvalidDataException("Stage is absent from the exact admitted WTT plan.");
            var saved = await ReadSavedWttPreparationAsync(intent, quote, step, plan.Sender, intent.SourceAuthority.Context,
                "Source", generation, intent.Source.DeploymentIdentity.GenesisHex, ct);
            if (saved is not null) return Ok(saved);
            var snapshot = await ReadWttSnapshotAsync(ct);
            if (WttDescriptor(snapshot) != intent.Source || snapshot.Deployment != plan.Deployment
                || await ReadWttAssetAsync(snapshot, intent.Asset.TokenAddress, intent.Asset.NativeAsset, ct) != intent.Asset
                || await ReadWttUIntAsync(snapshot.Deployment.Core, "messageFee()", [], snapshot.Block, ct) != WttPayloadCodec.ParseAmount(plan.MessageFee)
                || await ReadWttUIntAsync(snapshot.Deployment.TokenBridge, "finality()", [], snapshot.Block, ct) != intent.ConsistencyLevel)
                return Error<WttStagePreparation>("WTT deployment, asset behavior or publication costs changed before preparation.");
            var chain = await GetChainIdAsync(ct);
            if (chain.IsError) return Error<WttStagePreparation>(chain.Message);
            var allocatorProtocol = "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture);
            var key = WttPreparationKey(intent.OperationId, "Source", stage, generation);
            var id = EthereumIdentity(allocatorProtocol, plan.Sender.ToLowerInvariant(), key);
            var owner = WttOwnerIntent(intent, quote);
            var prepared = await PrepareCallAsync(key, plan.Sender, step.Target, WttPayloadCodec.ParseAmount(step.Value),
                Convert.FromHexString(step.Data[2..]), "WTT " + stage, intent.SourceAuthority.Context, ct, owner,
                new EthereumFeeAuthorization(WttPayloadCodec.ParseAmount(step.GasLimit), WttPayloadCodec.ParseAmount(plan.PriorityFee),
                    WttPayloadCodec.ParseAmount(plan.MaxFee), WttPayloadCodec.ParseAmount(step.Reservation.NetworkFee), WttPayloadCodec.ParseAmount(step.Value)));
            if (prepared.IsError || prepared.Result is null) return Error<WttStagePreparation>(prepared.Message);
            if (prepared.Result.Id != id || prepared.Result.TransactionId is null || prepared.Result.SignedTransactionBase64 is null)
                throw new InvalidOperationException("WTT preparation did not retain its exact nonce and signed candidate.");
            var recovery = new WttEvmRecovery(id, allocatorProtocol, plan.IntentHash, quote.QuoteId, stage, generation);
            return Ok(new WttStagePreparation(stage, generation, prepared.Result.TransactionId, prepared.Result.SignedTransactionBase64,
                plan.ProtocolIdentity, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(recovery)), step.Reservation));
        }
        catch (JsonException) { return Error<WttStagePreparation>("Malformed WTT source plan."); }
        catch (FormatException exception) { return Error<WttStagePreparation>(exception.Message); }
        catch (InvalidDataException exception) { return Error<WttStagePreparation>(exception.Message); }
    }

    /// <inheritdoc cref="IWttSourceModule.SubmitWttSourceAsync"/>
    public async Task SubmitWttSourceAsync(WttOperationIntent intent, WttStageQuote quote, WttStagePreparation prepared,
        CancellationToken ct = default)
    {
        var row = await ReadWttSourcePreparationAsync(intent, quote, prepared, ct);
        await RequireWttOriginalGenesisAsync(intent.Source.DeploymentIdentity.GenesisHex, ct);
        await SubmitPreparationAsync(row, ct);
    }

    private async Task<EthereumPreparation> ReadWttSourcePreparationAsync(WttOperationIntent intent, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct)
    {
        _ = RpcHttpClient;
        var plan = ReadWttSourcePlan(intent, quote);
        var step = plan.Steps.SingleOrDefault(value => value.Stage == prepared.Stage)
            ?? throw new InvalidDataException("Prepared stage is absent from the exact WTT plan.");
        return await ReadWttPreparationAsync(intent, quote, prepared, step, plan.Sender, intent.SourceAuthority.Context, "Source", ct);
    }

    private async Task<EthereumPreparation> ReadWttPreparationAsync(WttOperationIntent intent, WttStageQuote quote,
        WttStagePreparation prepared, WttEvmStep step, string sender, SigningContext context, string phase, CancellationToken ct)
    {
        var recovery = JsonSerializer.Deserialize<WttEvmRecovery>(prepared.RecoveryMetadata)
            ?? throw new InvalidDataException("WTT preparation recovery metadata is missing.");
        if (prepared.Generation < 0 || recovery.Generation != prepared.Generation || recovery.Stage != prepared.Stage
            || recovery.QuoteId != quote.QuoteId || recovery.IntentHash != quote.IntentHash || prepared.ProtocolIdentity != quote.ProtocolIdentity
            || prepared.Reservation != step.Reservation || prepared.AdditionalSignedTransactionsJson is not null)
            throw new InvalidDataException("WTT preparation differs from its immutable stage authorization.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError || recovery.AllocatorProtocol != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("WTT preparation belongs to another Ethereum network.");
        var key = WttPreparationKey(intent.OperationId, phase, prepared.Stage, prepared.Generation);
        var id = EthereumIdentity(recovery.AllocatorProtocol, sender.ToLowerInvariant(), key);
        if (id != recovery.PreparationId) throw new InvalidDataException("WTT recovery identity differs from the exact operation/stage/generation.");
        var row = await WithPreparationStoreAsync(store => store.GetAsync(id, ct));
        var fingerprint = EthereumIdentity(sender.ToLowerInvariant(), step.Target.ToLowerInvariant(), step.Value,
            step.Data, JsonSerializer.Serialize(context), WttOwnerIntent(intent, quote));
        if (row is null || row.CallFingerprint != fingerprint || row.SignedTransactionBase64 != prepared.SignedTransactionBase64
            || row.TransactionId != prepared.TransactionId || row.PreparationKey != key || row.ProtocolIdentity != recovery.AllocatorProtocol)
            throw new InvalidDataException("WTT wire does not match its durable nonce, intent and signature evidence.");
        return row;
    }

    private async Task<WttStagePreparation?> ReadSavedWttPreparationAsync(WttOperationIntent intent, WttStageQuote quote,
        WttEvmStep step, string sender, SigningContext context, string phase, int generation, string genesis, CancellationToken ct)
    {
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) throw new InvalidDataException(chain.Message);
        var protocol = "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture);
        var key = WttPreparationKey(intent.OperationId, phase, step.Stage, generation);
        var id = EthereumIdentity(protocol, sender.ToLowerInvariant(), key);
        var row = await WithPreparationStoreAsync(store => store.GetAsync(id, ct));
        if (row?.SignedTransactionBase64 is null || row.TransactionId is null) return null;
        var candidate = new WttStagePreparation(step.Stage, generation, row.TransactionId, row.SignedTransactionBase64,
            quote.ProtocolIdentity, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new WttEvmRecovery(id, protocol,
                quote.IntentHash, quote.QuoteId, step.Stage, generation))), step.Reservation);
        await RequireWttOriginalGenesisAsync(genesis, ct);
        _ = await ReadWttPreparationAsync(intent, quote, candidate, step, sender, context, phase, ct);
        return candidate;
    }

    private WttEvmPlan ReadWttSourcePlan(WttOperationIntent intent, WttStageQuote quote)
    {
        if (!ValidWttSourceIntent(intent) || quote is null || quote.Stages is null || quote.Phase != "Source"
            || quote.ProtocolIdentity != intent.Source.ProtocolIdentity || quote.IntentHash != CanonicalJson.Hash(intent))
            throw new InvalidDataException("WTT source quote does not bind the exact intent.");
        var plan = JsonSerializer.Deserialize<WttEvmPlan>(quote.PlanJson);
        if (plan is not { Version: 1, Steps: not null, Deployment: not null, AssetPin: not null } || plan.Steps.Count is < 1 or > 4
            || plan.Steps.Any(step => step is null || step.Reservation is null) || plan.IntentHash != quote.IntentHash
            || plan.Sender != intent.SourceAuthority.Address || plan.ProtocolIdentity != quote.ProtocolIdentity
            || CanonicalJson.Hash(plan.Deployment) != intent.Source.CodeIdentity
            || WttAssetBehaviorIdentity(intent.Asset, plan.Deployment, plan.AssetPin) != intent.Asset.BehaviorIdentity
            || quote.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan))
            || quote.QuoteId != CanonicalJson.Hash(new { Version = 1, IntentHash = plan.IntentHash, Plan = quote.PlanJson, ExpiresAt = quote.ExpiresAt })
            || !plan.Steps.Select(step => step.Reservation).SequenceEqual(quote.Stages))
            throw new InvalidDataException("WTT source plan identity or reservations changed.");
        if (WttPayloadCodec.Normalize(intent.Amount.Requested, DenormalizedWttFee(intent).ToString(CultureInfo.InvariantCulture),
                intent.Asset.Decimals) != intent.Amount || intent.RequiresAttestation && intent.Asset.OriginChain != intent.Source.ProtocolChain)
            throw new InvalidDataException("WTT normalization or origin attestation authority changed.");
        var stages = plan.Steps.Select(step => step.Stage).ToArray();
        var expected = new List<string>();
        if (stages.Contains("ResetAllowance")) expected.Add("ResetAllowance");
        if (stages.Contains("Approve")) expected.Add("Approve");
        if (intent.RequiresAttestation) expected.Add("Attest");
        expected.Add("Transfer");
        if (!stages.SequenceEqual(expected) || stages.Contains("ResetAllowance") && !stages.Contains("Approve")
            || intent.Asset.NativeAsset && stages.Any(stage => stage is "ResetAllowance" or "Approve"))
            throw new InvalidDataException("WTT prerequisite ordering changed.");
        var amount = WttPayloadCodec.ParseAmount(intent.Amount.Debited);
        var messageFee = WttPayloadCodec.ParseAmount(plan.MessageFee);
        var maxFee = WttPayloadCodec.ParseAmount(plan.MaxFee);
        if (maxFee < WttPayloadCodec.ParseAmount(plan.PriorityFee)) throw new InvalidDataException("Invalid WTT fee authorization.");
        BigInteger totalFee = 0, totalValue = 0;
        foreach (var step in plan.Steps)
        {
            var approval = step.Stage is "ResetAllowance" or "Approve";
            var target = approval ? intent.Asset.TokenAddress : intent.Source.TokenBridge;
            var value = approval ? BigInteger.Zero : messageFee + (step.Stage == "Transfer" && intent.Asset.NativeAsset ? amount : BigInteger.Zero);
            var data = step.Stage switch
            {
                "ResetAllowance" or "Approve" => EthereumAbi.EncodeCall("approve(address,uint256)", intent.Source.TokenBridge,
                    step.Stage == "ResetAllowance" ? BigInteger.Zero : amount),
                "Attest" => WttEthereumProtocol.AttestationCall(intent.Asset.TokenAddress, intent.Nonce),
                "Transfer" => WttEthereumProtocol.TransferCall(intent.Asset.TokenAddress, amount, intent.Target.ProtocolChain,
                    WttPayloadCodec.FixedHex(intent.RecipientProtocolAddress, 32), DenormalizedWttFee(intent), intent.Nonce, intent.Asset.NativeAsset),
                _ => throw new InvalidDataException("Unknown WTT source stage."),
            };
            var gas = WttPayloadCodec.ParseAmount(step.GasLimit);
            if (gas <= 0 || step.Target != target || step.Value != value.ToString(CultureInfo.InvariantCulture) || step.Data != data
                || step.Reservation != new WttStageReservation(step.Stage, (gas * maxFee).ToString(CultureInfo.InvariantCulture), "0", step.Value))
                throw new InvalidDataException("WTT call or reservation differs from its authorized semantics.");
            totalFee += gas * maxFee; totalValue += value;
        }
        if (totalFee > WttPayloadCodec.ParseAmount(intent.SourceBudget.MaximumNetworkFee)
            || totalValue > WttPayloadCodec.ParseAmount(intent.SourceBudget.MaximumValue))
            throw new InvalidDataException("WTT source plan exceeds caller costs.");
        return plan;
    }

    private async Task RequireWttOriginalGenesisAsync(string genesisHex, CancellationToken ct)
    {
        var genesis = await RpcReadAsync("eth_getBlockByNumber", ["0x0", false], ct);
        if (genesis.IsError || genesis.IsNullResult || !TryReadStringProperty(genesis.Value, "hash", out var actual)
            || !SameWttHash(actual, "0x" + genesisHex)) throw new InvalidDataException("WTT original genesis changed.");
    }

    /// <inheritdoc/>
    public async Task<WttNonceHold> ReadWttNonceHoldAsync(WttOperationIntent intent, string phase, string stage, int generation,
        CancellationToken ct = default)
    {
        var id = await WttPreparationRowIdAsync(intent, phase, stage, generation, ct);
        if (id is null) return WttNonceHold.Held;
        return (await WithPreparationStoreAsync(store => store.GetAsync(id, ct))) switch
        {
            null => WttNonceHold.None,
            { SignedTransactionBase64: null, TransactionId: null, State: "Released" or "ReleasedConsumedExternally" or "ReleasedRefilled" } => WttNonceHold.None,
            { SignedTransactionBase64: null, TransactionId: null, State: "Reserved" } => WttNonceHold.Unsigned,
            _ => WttNonceHold.Held,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> ReleaseUnsignedWttReservationAsync(WttOperationIntent intent, string phase, string stage, int generation,
        CancellationToken ct = default)
        => await WttPreparationRowIdAsync(intent, phase, stage, generation, ct) is { } id && await ReleaseUnsignedReservationAsync(id, ct);

    /// <summary>The allocator row id the preparation path derives for this exact operation/phase/stage/generation and payer.</summary>
    private async Task<string?> WttPreparationRowIdAsync(WttOperationIntent intent, string phase, string stage, int generation,
        CancellationToken ct)
    {
        var sender = phase switch { "Source" => intent.SourceAuthority.Address, "Target" => intent.TargetAuthority.Address, _ => null };
        if (sender is null || generation < 0 || string.IsNullOrWhiteSpace(stage)) return null;
        var chain = await GetChainIdAsync(ct);
        return chain.IsError ? null : EthereumIdentity("eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture),
            sender.ToLowerInvariant(), WttPreparationKey(intent.OperationId, phase, stage, generation));
    }

    private static string WttOwnerIntent(WttOperationIntent intent, WttStageQuote quote)
        => CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(new { Intent = intent, Quote = quote }));
    private static string WttPreparationKey(string operationId, string phase, string stage, int generation)
        => "wtt:" + CanonicalJson.Hash(new { OperationId = operationId, Phase = phase, Stage = stage, Generation = generation });
}
