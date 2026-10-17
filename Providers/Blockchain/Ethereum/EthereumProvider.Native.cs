using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

public partial class EthereumProvider : INativeOperationModule, IReadOnlyContractModule
{
    private static readonly JsonSerializerOptions NativeJson = new(JsonSerializerDefaults.Web);
    private sealed record NativeEvmCall(string Sender, string? Target, BigInteger Value, byte[] Data,
        string ExpectedCode, SigningContext Context);
    private sealed record NativeEvmPlan(string GasLimit, string PriorityFee, string MaxFee);
    private sealed record NativeEvmRecovery(string PreparationId, string IntentHash);

    /// <inheritdoc/>
    public Task<AZOAResult<NativeArtifact>> ValidateArtifactAsync(NativeArtifact artifact, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TryNativeArtifact(artifact, out _, out _, out _, out var error)
            ? Ok(artifact) : Error<NativeArtifact>(error));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeOperationQuote>> QuoteNativeOperationAsync(NativeOperationIntent intent, CancellationToken ct = default)
    {
        if (!TryNativeCall(intent, out var call, out var error)) return Error<NativeOperationQuote>(error);
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError) return Error<NativeOperationQuote>(chain.Message);
        if (call.Target is not null && !await NativeTargetMatchesAsync(call, "latest", ct))
            return Error<NativeOperationQuote>("The target contract code differs from the authorized artifact.");
        var fees = await GetFeeDataAsync(ct);
        var gas = await EstimateGasAsync(call.Sender, call.Target, call.Value, call.Data, "native operation quote", ct);
        if (fees.IsError || fees.Result is null || gas.IsError)
            return Error<NativeOperationQuote>(fees.IsError ? fees.Message : gas.Message);
        var plan = new NativeEvmPlan(gas.Result.ToString(CultureInfo.InvariantCulture),
            fees.Result.MaxPriorityFeePerGas.ToString(CultureInfo.InvariantCulture), fees.Result.MaxFeePerGas.ToString(CultureInfo.InvariantCulture));
        return Ok(new NativeOperationQuote("", "", "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture),
            (gas.Result * fees.Result.MaxFeePerGas).ToString(CultureInfo.InvariantCulture), "0",
            call.Value.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(plan), DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeNextStage>> GetNextStageAsync(NativeOperationIntent intent, IReadOnlyList<NativeStageHistory> history, CancellationToken ct = default)
    {
        if (!TryNativeCall(intent, out _, out var error)) return Error<NativeNextStage>(error);
        if (history.Count == 0) return Ok(new NativeNextStage("Execute", false));
        if (history.Count != 1 || history[0].Prepared.Stage != "Execute")
            return Error<NativeNextStage>("Ethereum native history contains an unexpected stage.");
        var observed = await ObserveNativeStageAsync(intent, history[0].Prepared, ct);
        return observed.IsError || observed.Result is null ? Error<NativeNextStage>(observed.Message)
            : observed.Result.Verdict == "Confirmed"
                ? Ok(new NativeNextStage(null, true, observed.Result.ResourceId, observed.Result.EvidenceJson))
                : Error<NativeNextStage>("The exact Ethereum operation is not confirmed.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeStagePreparation>> PrepareNativeStageAsync(NativeOperationIntent intent, string stage, int generation, CancellationToken ct = default)
    {
        if (stage != "Execute" || generation != 0 || !TryNativeCall(intent, out var call, out _))
            return Error<NativeStagePreparation>("Ethereum native stage or intent is invalid.");
        var preparationKey = "native:" + intent.OperationKey + ":0";
        var existing = await WithPreparationStoreAsync(store => store.GetAsync(
            EthereumIdentity(intent.ProtocolIdentity, call.Sender.ToLowerInvariant(), preparationKey), ct));
        if (!NativeOperationAuthorization.ValidQuote(intent, false))
            return Error<NativeStagePreparation>("An exact, current native quote is required before nonce allocation.");
        var quote = intent.Quote!;
        var plan = JsonSerializer.Deserialize<NativeEvmPlan>(quote.PlanJson);
        if (plan is null || !NativeOperationAuthorization.Amount(plan.GasLimit, out var gas)
            || !NativeOperationAuthorization.Amount(plan.PriorityFee, out var priority)
            || !NativeOperationAuthorization.Amount(plan.MaxFee, out var maxFee)
            || gas * maxFee != BigInteger.Parse(quote.NetworkFee, CultureInfo.InvariantCulture)
            || quote.Value != call.Value.ToString(CultureInfo.InvariantCulture) || quote.LockedRent != "0")
            return Error<NativeStagePreparation>("Ethereum quote cost does not match its immutable transaction plan.");
        if (existing?.SignedTransactionBase64 is null && call.Target is not null && !await NativeTargetMatchesAsync(call, "latest", ct))
            return Error<NativeStagePreparation>("Target contract code changed before preparation.");
        var chain = await GetChainIdAsync(ct);
        if (chain.IsError || intent.ProtocolIdentity != "eip155:" + chain.Result.ToString(CultureInfo.InvariantCulture))
            return Error<NativeStagePreparation>("Ethereum native quote belongs to another chain.");
        var feeAuthorization = new EthereumFeeAuthorization(gas, priority, maxFee,
            BigInteger.Parse(intent.Budget.MaximumNetworkFee, CultureInfo.InvariantCulture),
            BigInteger.Parse(intent.Budget.MaximumValue, CultureInfo.InvariantCulture));
        var prepared = await PrepareCallAsync(preparationKey, call.Sender, call.Target, call.Value,
            call.Data, "authorized native " + intent.Kind, call.Context, ct, CanonicalJson.Hash(intent), feeAuthorization);
        if (prepared.IsError || prepared.Result is null) return Error<NativeStagePreparation>(prepared.Message);
        var row = prepared.Result;
        return Ok(new NativeStagePreparation(stage, row.TransactionId!, row.SignedTransactionBase64!, row.ProtocolIdentity,
            JsonSerializer.Serialize(new NativeEvmRecovery(row.Id, CanonicalJson.Hash(intent))), quote.NetworkFee, "0", ReservedValue: quote.Value));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> SubmitNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default)
    {
        var row = await ReadNativePreparationAsync(intent, prepared, ct);
        await SubmitPreparationAsync(row, ct);
        return Ok(prepared.TransactionId);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeStageObservation>> ObserveNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default)
    {
        var row = await ReadNativePreparationAsync(intent, prepared, ct);
        var observed = await ObservePreparationAsync(row, ct);
        if (observed.IsError) return Error<NativeStageObservation>(observed.Message);
        if (observed.Result == ChainConfirmation.Unknown)
        {
            foreach (var cancellation in JsonSerializer.Deserialize<List<EthereumCancellation>>(row.RecoveryCandidatesJson) ?? [])
            {
                var candidate = CancellationPreparation(row, cancellation);
                var canceled = await ObservePreparationAsync(candidate, ct);
                if (canceled.IsError) return Error<NativeStageObservation>(canceled.Message);
                if (canceled.Result is not (ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain)) continue;
                var cancellationProof = await ReadNativeFinalizedReceiptAsync(candidate.TransactionId!, canceled.Result, ct);
                if (cancellationProof is null) return Error<NativeStageObservation>("Winning cancellation receipt requires reconciliation.");
                return Ok(new NativeStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity,
                    EvidenceJson: JsonSerializer.Serialize(new { canceledOriginal = prepared.TransactionId,
                        winningNonceTransaction = candidate.TransactionId, cancellationProof.BlockHash, cancellationProof.BlockNumber,
                        cancellationSucceeded = canceled.Result == ChainConfirmation.Confirmed }),
                    Detail: "A finalized same-nonce cancellation consumed the original transaction nonce.",
                    Accounting: new NativeStageAccounting(cancellationProof.NetworkFee, [])));
            }
        }
        if (observed.Result is not (ChainConfirmation.Confirmed or ChainConfirmation.FailedOnChain))
            return Ok(new NativeStageObservation(row.State is "Canceling" or "Canceled" ? "Unknown" : "Pending",
                prepared.TransactionId, prepared.ProtocolIdentity, Detail: "Exact nonce-owned transaction awaits finality."));
        if (!TryNativeCall(intent, out var call, out var error)) return Error<NativeStageObservation>(error);
        var finalizedReceipt = await ReadNativeFinalizedReceiptAsync(prepared.TransactionId, observed.Result, ct);
        if (finalizedReceipt is null) return Error<NativeStageObservation>("Finalized native receipt or fee evidence is unavailable.");
        var receipt = finalizedReceipt.Receipt;
        var block = finalizedReceipt.BlockNumber;
        var blockHash = finalizedReceipt.BlockHash;
        var accounting = new NativeStageAccounting(finalizedReceipt.NetworkFee, []);
        if (observed.Result == ChainConfirmation.FailedOnChain)
            return Ok(new NativeStageObservation("FailedOnChain", prepared.TransactionId, prepared.ProtocolIdentity, Accounting: accounting));
        var resource = call.Target;
        if (resource is null && (!TryReadStringProperty(receipt, "contractAddress", out resource)
            || !EthereumAddress.TryParse(resource, out _)))
            return Error<NativeStageObservation>("Finalized creation receipt does not identify a deployed contract.");
        var proof = call with { Target = resource };
        if (!await NativeTargetMatchesAsync(proof, block, ct))
            return Error<NativeStageObservation>("Finalized runtime code differs from the exact artifact descriptor.");
        return Ok(new NativeStageObservation("Confirmed", prepared.TransactionId, prepared.ProtocolIdentity, resource,
            JsonSerializer.Serialize(new { transactionId = prepared.TransactionId, blockHash, blockNumber = block,
                contractAddress = resource, expectedCode = call.ExpectedCode, artifactHash = intent.ArtifactHash }), Accounting: accounting));
    }

    private async Task<EthereumPreparation> ReadNativePreparationAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct)
    {
        if (prepared.Stage != "Execute" || !NativeOperationAuthorization.ValidQuote(intent, false)
            || !TryNativeCall(intent, out var call, out _) || prepared.ProtocolIdentity != intent.ProtocolIdentity
            || prepared.AdditionalSignedTransactionsJson is not null || prepared.ReservedRent != "0"
            || prepared.ReservedFee != intent.Quote!.NetworkFee || prepared.ReservedValue != intent.Quote.Value)
            throw new InvalidOperationException("Prepared Ethereum native authorization is invalid.");
        var recovery = JsonSerializer.Deserialize<NativeEvmRecovery>(prepared.RecoveryMetadata)
            ?? throw new InvalidOperationException("Ethereum native recovery identity is missing.");
        var expectedId = EthereumIdentity(intent.ProtocolIdentity, call.Sender.ToLowerInvariant(), "native:" + intent.OperationKey + ":0");
        var intentHash = CanonicalJson.Hash(intent);
        if (recovery.PreparationId != expectedId || recovery.IntentHash != intentHash)
            throw new InvalidOperationException("Ethereum native recovery identity was changed.");
        var row = await WithPreparationStoreAsync(store => store.GetAsync(expectedId, ct));
        var fingerprint = EthereumIdentity(call.Sender.ToLowerInvariant(), call.Target?.ToLowerInvariant() ?? "",
            call.Value.ToString(CultureInfo.InvariantCulture), EthereumHex.Data(call.Data), JsonSerializer.Serialize(call.Context), intentHash);
        if (row is null || row.CallFingerprint != fingerprint || row.SignedTransactionBase64 != prepared.SignedTransactionBase64
            || row.TransactionId != prepared.TransactionId || row.ProtocolIdentity != prepared.ProtocolIdentity)
            throw new InvalidOperationException("Ethereum native wire differs from the durable signed candidate.");
        return row;
    }

    private async Task<bool> NativeTargetMatchesAsync(NativeEvmCall call, string block, CancellationToken ct)
    {
        var code = await RpcCallAsync("eth_getCode", new object?[] { call.Target, block }, ct);
        if (code.IsError || !code.TryGetString(out var text) || !EthereumHex.TryParseData(text, out var bytes) || bytes.Length == 0) return false;
        return call.ExpectedCode.StartsWith("keccak:", StringComparison.Ordinal)
            ? EthereumHex.Data(Keccak.CalculateHash(bytes)).Equals(call.ExpectedCode[7..], StringComparison.OrdinalIgnoreCase)
            : EthereumHex.Data(bytes).Equals(call.ExpectedCode, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryNativeCall(NativeOperationIntent intent, out NativeEvmCall call, out string error)
    {
        call = null!; error = "Ethereum native intent is invalid.";
        if (intent.Version != 1 || intent.ChainType != ChainType || intent.Network != ActiveNetwork.ToString()
            || string.IsNullOrWhiteSpace(intent.OperationKey) || intent.Authorities.Count != 1) return false;
        EvmNativeInput? input;
        try { input = intent.Input.Deserialize<EvmNativeInput>(NativeJson); }
        catch (JsonException) { return false; }
        if (input is null || !EthereumAddress.TryParse(input.Sender, out var sender)
            || !NativeOperationAuthorization.Amount(input.ValueBaseUnits, out var value) || value > EthereumHex.MaxUInt256
            || !TryNativeArtifact(input.Artifact, out var target, out var data, out var code, out error)) return false;
        var authority = intent.Authorities[0];
        var scope = target is null ? AzoaScopes.ContractDeploySign : AzoaScopes.ContractCallSign;
        if (!EthereumAddress.AreSame(sender, authority.Address) || authority.SigningContext.IsPlatform || !authority.SigningContext.IsResolvableUserContext
            || authority.SigningContext.Scope != scope || intent.Kind != (target is null ? "Deploy" : "Call")) return false;
        call = new NativeEvmCall(sender, target, value, data, code, authority.SigningContext);
        return true;
    }

    private static bool TryNativeArtifact(NativeArtifact artifact, out string? target, out byte[] data, out string code, out string error)
    {
        target = null; data = []; code = ""; error = "A typed EVM creation or call artifact with exact code identity is required.";
        try
        {
            if (artifact.Format == "evm-contract/1")
            {
                var creation = artifact.Content.Deserialize<EvmContractArtifact>(NativeJson);
                if (creation is null || !EthereumHex.TryParseData(creation.InitCodeHex, out var init) || init.Length is 0 or > 49152
                    || !EthereumHex.TryParseData(creation.ExpectedRuntimeCodeHex, out var runtime) || runtime.Length is 0 or > 24576) return false;
                var values = creation.ConstructorArguments ?? [];
                if (creation.ConstructorSignature is null)
                {
                    if (values.Count != 0) return false;
                    data = init;
                }
                else
                {
                    if (!creation.ConstructorSignature.StartsWith("constructor(", StringComparison.Ordinal)
                        || !TryNativeArguments(creation.ConstructorSignature, values, out var arguments)) return false;
                    data = [.. init, .. arguments[4..]];
                    if (data.Length > 49152) return false;
                }
                code = EthereumHex.Data(runtime);
                return true;
            }
            if (artifact.Format == "evm-call/1")
            {
                var invocation = artifact.Content.Deserialize<EvmCallArtifact>(NativeJson);
                if (invocation is null || !EthereumAddress.TryParse(invocation.ContractAddress, out var address)
                    || !EthereumHex.TryParseData(invocation.ExpectedCodeHash, out var hash) || hash.Length != 32
                    || !TryNativeArguments(invocation.Method, invocation.Arguments, out data)) return false;
                target = address; code = "keccak:" + EthereumHex.Data(hash);
                return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryNativeArguments(string signatureText, IReadOnlyList<JsonElement> values, out byte[] encoded)
    {
        encoded = [];
        var signature = EthereumAbi.ParseSignature(signatureText);
        if (signature is null || signature.Parameters.Length != values.Count) return false;
        var arguments = new object[values.Count];
        for (var index = 0; index < values.Count; index++)
            if (!EthereumAbi.TryCoerceArgument(signature.Parameters[index].Type, values[index], out arguments[index], out _)) return false;
        try { return EthereumHex.TryParseData(EthereumAbi.EncodeCall(signature, arguments), out encoded); }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
}
