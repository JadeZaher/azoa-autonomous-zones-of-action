using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Providers.Blockchain.ClientSigning;
using AZOA.WebAPI.Services.Reconciliation;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Blockchain.ClientSigning;

/// <summary>What one reconciliation pass did to a client-signed operation.</summary>
public enum ClientSigningReconcileOutcome { Unchanged, Completed, Failed, Parked, Flagged }

/// <summary>
/// D-3 lifecycle of client-signed operations: exact-byte submission, chain-proven terminal outcomes, stuck-row parking and
/// operator resolution. See <c>Services/Blockchain/AGENTS.md</c> §client-signed operations.
/// </summary>
public sealed class ClientSignedOperationService(
    IBlockchainOperationStore operations,
    IClientSignedOperationStore transitions,
    IBlockchainProviderFactory providers,
    IIdempotencyStore idempotency,
    IOptions<ReconciliationOptions> options,
    ILogger<ClientSignedOperationService> logger)
{
    private const int MaxReasonLength = 500;
    private static readonly string[] Submittable = [OperationStatus.AwaitingSignature, ClientSigningStatus.NeedsOperator];
    private static readonly string[] Resolvable =
        [OperationStatus.AwaitingSignature, OperationStatus.PendingConfirmation, ClientSigningStatus.NeedsOperator];

    private TimeSpan SignatureTtl => TimeSpan.FromSeconds(Math.Max(0, options.Value.ClientSignatureTtlSeconds));

    /// <summary>The saved plan of an operation the caller owns.</summary>
    public async Task<AZOAResult<ClientSigningPlanView>> GetPlanAsync(Guid operationId, Guid caller, Guid? actingTenant, CancellationToken ct)
    {
        var owned = await LoadOwnedAsync(operationId, caller, actingTenant, ct);
        return owned is { } found && ReadPlan(found) is { } plan
            ? AZOAResult<ClientSigningPlanView>.Success(ClientSigningPlanView.From(found, plan))
            : AZOAResult<ClientSigningPlanView>.FailureWithCode("Client-signed operation not found.", AzoaErrorCodes.NotFound);
    }

    /// <summary>
    /// Accepts the plan's exact bytes signed by its signer, persists them before the first broadcast, then broadcasts.
    /// Re-submitting the same bytes is idempotent (re-broadcast while unconfirmed); different bytes are refused.
    /// </summary>
    public async Task<AZOAResult<IBlockchainOperation>> SubmitAsync(Guid operationId, Guid caller, Guid? actingTenant,
        string signedTransactionBase64, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(signedTransactionBase64) || signedTransactionBase64.Length > 16_384)
            return Refused("Signed transaction bytes are required.", AzoaErrorCodes.InvalidRequest);
        var operation = await LoadOwnedAsync(operationId, caller, actingTenant, ct);
        if (operation is null || ReadPlan(operation) is not { } plan)
            return Refused("Client-signed operation not found.", AzoaErrorCodes.NotFound);
        if (Module(plan) is not { } module)
            return Refused("Client signing is unavailable on this operation's network.", AzoaErrorCodes.DependencyUnavailable);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (operation.Parameters.TryGetValue(ClientSigningParameters.SignedTransaction, out var saved))
                return await ReplayAsync(operation, plan, module, saved, signedTransactionBase64, ct);
            if (!Submittable.Contains(operation.Status))
                return Refused($"The operation is {operation.Status}; it no longer accepts a signature.", AzoaErrorCodes.Conflict);
            var verified = module.VerifyClientSignedTransfer(plan, signedTransactionBase64);
            if (verified.IsError || verified.Result is null)
                return Refused(verified.Message, ClientSigningCodes.SignatureMismatch);
            var parameters = With(operation, (ClientSigningParameters.SignedTransaction, signedTransactionBase64),
                (ClientSigningParameters.TransactionHash, verified.Result));
            if (await transitions.TryTransitionAsync(operation.Id, operation.Status, OperationStatus.PendingConfirmation, parameters, null, ct))
            {
                var broadcast = await module.SubmitClientSignedTransferAsync(plan, signedTransactionBase64, ct);
                if (broadcast.IsError)
                    logger.LogWarning("Client-signed operation {OperationId} broadcast unconfirmed ({Code}); reconciliation settles it from chain truth",
                        operation.Id, broadcast.Code);
                return await ReloadAsync(operation.Id, ct);
            }
            operation = await operations.GetByIdAsync(operation.Id, ct) is { IsError: false, Result: { } current } ? current : null;
            if (operation is null) return Refused("Client-signed operation not found.", AzoaErrorCodes.NotFound);
        }
        return Refused("The operation changed concurrently; reload it.", AzoaErrorCodes.Conflict);
    }

    /// <summary>One reconciliation pass over a client-signed row (background sweep or a receipt-triggered check).</summary>
    public async Task<ClientSigningReconcileOutcome> ReconcileAsync(IBlockchainOperation operation, DateTime now, CancellationToken ct)
    {
        if (ReadPlan(operation) is not { } plan) return ClientSigningReconcileOutcome.Flagged;
        if (operation.Status is not (OperationStatus.AwaitingSignature or OperationStatus.PendingConfirmation))
            return ClientSigningReconcileOutcome.Unchanged;
        var stale = now - operation.CreatedDate >= SignatureTtl;
        if (operation.Status == OperationStatus.AwaitingSignature && !stale) return ClientSigningReconcileOutcome.Unchanged;
        if (Module(plan) is not { } module) return ClientSigningReconcileOutcome.Flagged;
        var observation = await ObserveAsync(module, plan, operation.Parameters.GetValueOrDefault(ClientSigningParameters.TransactionHash), ct);
        if (observation is null) return ClientSigningReconcileOutcome.Flagged;
        if (observation.Verdict == ClientSigningVerdicts.Pending
            || (observation.Verdict == ClientSigningVerdicts.NonceConsumed && !Attributable(operation, observation)))
        {
            // An unexpiring signature (Ethereum) or an unattributed nonce: only an operator, or the signer submitting, settles it.
            var unattributed = observation.Verdict == ClientSigningVerdicts.NonceConsumed;
            var park = operation.Status == OperationStatus.AwaitingSignature && stale && (plan.ValidThroughHeight is null || unattributed)
                || operation.Status == OperationStatus.PendingConfirmation && unattributed;
            if (!park) return ClientSigningReconcileOutcome.Unchanged;
            var parked = await transitions.TryTransitionAsync(operation.Id, operation.Status, ClientSigningStatus.NeedsOperator,
                With(operation, (ClientSigningParameters.Error, observation.Detail ?? "The signature has not reached the chain and never expires.")),
                null, ct);
            return parked ? ClientSigningReconcileOutcome.Parked : ClientSigningReconcileOutcome.Unchanged;
        }
        return await ApplyTerminalAsync(operation, observation, null, ct);
    }

    /// <summary>Stuck rows for the operator console: unsigned past the TTL, or parked.</summary>
    public async Task<IReadOnlyList<StuckClientSignedOperationView>> ListStuckAsync(int limit, CancellationToken ct)
    {
        var ids = await transitions.ListStuckAsync(DateTimeOffset.UtcNow - SignatureTtl, limit, ct);
        var views = new List<StuckClientSignedOperationView>();
        foreach (var id in ids)
        {
            if (await operations.GetByIdAsync(id, ct) is not { IsError: false, Result: { } operation } || ReadPlan(operation) is not { } plan)
                continue;
            views.Add(new(operation.Id, operation.UserId, operation.Status, operation.CreatedDate, plan.ChainType, plan.Network, plan.Signer,
                operation.Parameters.GetValueOrDefault(ClientSigningParameters.TransactionHash), plan.Nonce, plan.ValidThroughHeight,
                operation.Parameters.ContainsKey(ClientSigningParameters.SignedTransaction), operation.Parameters.GetValueOrDefault(ClientSigningParameters.Error)));
        }
        return views;
    }

    /// <summary>
    /// Operator resolution: re-observes the chain (with an optionally supplied transaction id) and applies only an outcome the
    /// chain proves. The operator's word never settles anything by itself.
    /// </summary>
    public async Task<AZOAResult<IBlockchainOperation>> ResolveAsync(Guid operationId, Guid operatorId, ClientSigningResolution resolution,
        CancellationToken ct)
    {
        if (resolution is null || string.IsNullOrWhiteSpace(resolution.Reason) || resolution.Reason.Length > MaxReasonLength
            || resolution.TransactionId is { Length: > 128 })
            return Refused($"A reason of at most {MaxReasonLength} characters is required.", AzoaErrorCodes.InvalidRequest);
        var operation = await operations.GetByIdAsync(operationId, ct) is { IsError: false, Result: { } found } ? found : null;
        if (operation is null || ReadPlan(operation) is not { } plan) return Refused("Client-signed operation not found.", AzoaErrorCodes.NotFound);
        if (!Resolvable.Contains(operation.Status))
            return Refused($"The operation is {operation.Status}; there is nothing to resolve.", AzoaErrorCodes.Conflict);
        if (Module(plan) is not { } module)
            return Refused("Client signing is unavailable on this operation's network.", AzoaErrorCodes.DependencyUnavailable);
        // A submitted row is judged by its own bytes first; an operator-supplied id can only attribute a consumed nonce to a
        // different transaction from the same signer at the same nonce (re-proven by the module).
        var own = operation.Parameters.GetValueOrDefault(ClientSigningParameters.TransactionHash);
        var supplied = string.IsNullOrWhiteSpace(resolution.TransactionId) ? null : resolution.TransactionId.Trim();
        var observation = await ObserveAsync(module, plan, own ?? supplied, ct);
        if (observation is { Verdict: ClientSigningVerdicts.NonceConsumed, TransactionId: null } && own is not null && supplied is not null
            && !string.Equals(own, supplied, StringComparison.OrdinalIgnoreCase))
            observation = await ObserveAsync(module, plan, supplied, ct);
        if (observation is null || observation.Verdict == ClientSigningVerdicts.Pending
            || (observation.Verdict == ClientSigningVerdicts.NonceConsumed && !Attributable(operation, observation)))
            return Refused("Chain evidence does not settle this operation yet"
                + (observation?.Detail is { } detail ? ": " + detail : "."), AzoaErrorCodes.Conflict);
        var audit = JsonSerializer.Serialize(new { operatorId, reason = resolution.Reason.Trim(), observation.Verdict,
            transactionId = observation.TransactionId, at = DateTimeOffset.UtcNow });
        var outcome = await ApplyTerminalAsync(operation, observation, audit, ct);
        logger.LogWarning("Operator {OperatorId} resolved client-signed operation {OperationId} from chain verdict {Verdict}: {Outcome}",
            operatorId, operationId, observation.Verdict, outcome);
        return outcome is ClientSigningReconcileOutcome.Completed or ClientSigningReconcileOutcome.Failed
            ? await ReloadAsync(operationId, ct)
            : Refused("The operation changed concurrently; reload it.", AzoaErrorCodes.Conflict);
    }

    /// <summary>Reads the saved plan of a client-signed row, or null for any other row.</summary>
    public static ClientSigningPlan? ReadPlan(IBlockchainOperation operation)
    {
        if (!operation.Parameters.TryGetValue(ClientSigningParameters.Plan, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ClientSigningPlan>(json); }
        catch (JsonException) { return null; }
    }

    private async Task<ClientSigningReconcileOutcome> ApplyTerminalAsync(IBlockchainOperation operation, ClientSigningObservation observation,
        string? audit, CancellationToken ct)
    {
        var confirmed = observation.Verdict == ClientSigningVerdicts.Confirmed;
        var updates = new List<(string, string)>();
        if (observation.TransactionId is { } id && observation.Verdict is ClientSigningVerdicts.Confirmed or ClientSigningVerdicts.FailedOnChain)
            updates.Add((ClientSigningParameters.TransactionHash, id));
        if (!confirmed)
        {
            var (outcome, error) = observation.Verdict switch
            {
                ClientSigningVerdicts.FailedOnChain => ("FailedOnChain", "The signed transaction failed on chain; no value moved."),
                ClientSigningVerdicts.Expired => ("Abandoned", "The signature window expired without the transaction landing; no value moved."),
                _ => ("Superseded", "A different transaction consumed the plan's nonce; the signed transfer can never land."),
            };
            updates.Add((ClientSigningParameters.Outcome, outcome));
            updates.Add((ClientSigningParameters.Error, error));
        }
        if (audit is not null) updates.Add((ClientSigningParameters.OperatorResolution, audit));
        var next = confirmed ? OperationStatus.Completed : OperationStatus.Failed;
        if (!await transitions.TryTransitionAsync(operation.Id, operation.Status, next, With(operation, [.. updates]), DateTimeOffset.UtcNow, ct))
            return ClientSigningReconcileOutcome.Unchanged;
        var payload = confirmed ? observation.TransactionId ?? string.Empty : updates.First(item => item.Item1 == ClientSigningParameters.Error).Item2;
        foreach (var key in new[] { ClientSigningParameters.ExecutionIdempotencyKey, "IdempotencyKey" })
            if (operation.Parameters.GetValueOrDefault(key) is { Length: > 0 } claim)
                await SettleAsync(claim, confirmed, payload, operation.Id, ct);
        return confirmed ? ClientSigningReconcileOutcome.Completed : ClientSigningReconcileOutcome.Failed;
    }

    /// <summary>
    /// A consumed nonce settles the row only when the consumer is a found, different transaction. An unattributed consumer might be
    /// the plan's own transaction (a node that cannot look it up), and failing it then would invite a double payment.
    /// </summary>
    private static bool Attributable(IBlockchainOperation operation, ClientSigningObservation observation)
        => observation.TransactionId is not null;

    private async Task<ClientSigningObservation?> ObserveAsync(IClientSignedTransferModule module, ClientSigningPlan plan, string? transactionId,
        CancellationToken ct)
    {
        try
        {
            var observed = await module.ObserveClientSignedTransferAsync(plan, transactionId, ct);
            if (!observed.IsError && observed.Result is not null) return observed.Result;
            logger.LogWarning("Client-signed plan observation refused: {Message}", observed.Message);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException
            or InvalidDataException or FormatException or KeyNotFoundException)
        {
            logger.LogWarning(exception, "Client-signed plan observation failed; the row stays as it is");
            return null;
        }
    }

    private async Task<AZOAResult<IBlockchainOperation>> ReplayAsync(IBlockchainOperation operation, ClientSigningPlan plan,
        IClientSignedTransferModule module, string saved, string submitted, CancellationToken ct)
    {
        if (!string.Equals(saved, submitted, StringComparison.Ordinal))
            return Refused("A different signature was already submitted for this operation.", AzoaErrorCodes.Conflict);
        if (operation.Status == OperationStatus.PendingConfirmation)
        {
            var broadcast = await module.SubmitClientSignedTransferAsync(plan, saved, ct);
            if (broadcast.IsError)
                logger.LogInformation("Re-broadcast of client-signed operation {OperationId} unconfirmed ({Code})", operation.Id, broadcast.Code);
        }
        return AZOAResult<IBlockchainOperation>.Success(operation, "Duplicate submission: the original signed bytes stand.");
    }

    private async Task SettleAsync(string key, bool completed, string payload, Guid operationId, CancellationToken ct)
    {
        try
        {
            if (await idempotency.GetAsync(key, ct) is not { State: IdempotencyState.InProgress }) return;
            if (completed) await idempotency.CompleteAsync(key, payload, ct);
            else await idempotency.FailAsync(key, payload, ct);
        }
        catch (InvalidOperationException exception)
        {
            // The store reports a vanished claim this way; the row's terminal state already committed.
            logger.LogWarning(exception, "Idempotency claim of client-signed operation {OperationId} was already gone", operationId);
        }
    }

    private IClientSignedTransferModule? Module(ClientSigningPlan plan)
    {
        if (!Enum.TryParse<ChainNetwork>(plan.Network, false, out var network) || !Enum.IsDefined(network)) return null;
        try
        {
            var provider = providers.GetProvider(plan.ChainType, network);
            return provider.TryGetModule<IClientSignedTransferModule>(out var module) ? module : null;
        }
        catch (BlockchainProviderNotFoundException) { return null; }
    }

    private async Task<IBlockchainOperation?> LoadOwnedAsync(Guid operationId, Guid caller, Guid? actingTenant, CancellationToken ct)
    {
        var loaded = await operations.GetByIdAsync(operationId, ct);
        return loaded is { IsError: false, Result: { } operation } && caller != Guid.Empty && operation.UserId == caller
            && (operation.ActingTenantId ?? Guid.Empty) == (actingTenant ?? Guid.Empty) ? operation : null;
    }

    private async Task<AZOAResult<IBlockchainOperation>> ReloadAsync(Guid operationId, CancellationToken ct)
        => await operations.GetByIdAsync(operationId, ct) is { IsError: false, Result: { } current }
            ? AZOAResult<IBlockchainOperation>.Success(current)
            : Refused("Operation outcome is unavailable.", AzoaErrorCodes.DependencyUnavailable);

    private static Dictionary<string, string> With(IBlockchainOperation operation, params (string Key, string Value)[] updates)
    {
        var parameters = new Dictionary<string, string>(operation.Parameters, StringComparer.Ordinal);
        foreach (var (key, value) in updates) parameters[key] = value;
        return parameters;
    }

    private static AZOAResult<IBlockchainOperation> Refused(string message, string code)
        => AZOAResult<IBlockchainOperation>.FailureWithCode(message, code);
}

/// <summary>Operation statuses introduced by D-3 that the shared <see cref="OperationStatus"/> set does not list yet.</summary>
public static class ClientSigningStatus
{
    /// <summary>Parked: only an operator (re-proving chain state) or the signer submitting can settle the row.</summary>
    public const string NeedsOperator = "NeedsOperator";
}
