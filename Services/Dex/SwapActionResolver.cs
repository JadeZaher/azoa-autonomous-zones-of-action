using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Dex;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Services.Dex;

/// <summary>Admits and advances workflow swaps under the frozen node witness; successors receive only the confirmed receipt.</summary>
public sealed class SwapActionResolver(DexQuoteService quotes, SwapOperationService swaps, ISwapOperationStore operations,
    IActionNodeExecutionStore executions, DexModuleResolver modules) : IActionExternalOperationResolver
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public string Domain => "swap";

    public static string RequestKey(Guid run, Guid node) => $"action:{run:N}:{node:N}:swap";

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
    {
        var key = RequestKey(context.RunId, context.NodeId);
        return new(1, "swap", key, SwapOperationIdentity.Operation(context.ActingUserId, context.ActingTenantId, SwapOperationIdentity.RequestKey(key)),
            intentHash, context.ActingUserId, context.ActingTenantId);
    }

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var loaded = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (loaded.IsError || loaded.Result?.ExternalOperation is not { } reference || loaded.Result.FrozenInput is not { } frozen ||
            Reference(context, reference.IntentHash) != reference)
            return ActionNodeHandlerResult.Invalid("Swap effects require original durable workflow admission.");
        var observed = await ObserveAsync(reference, frozen, ct);
        return observed.Status == ActionExternalOperationStatus.Succeeded
            ? ActionNodeHandlerResult.Ok(observed.Output, txHash: observed.TransactionId, chainType: observed.ChainType)
            : ActionNodeHandlerResult.Pending(observed);
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        ActionFrozenNodeInput? input;
        try { input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput); }
        catch (JsonException) { input = null; }
        if (input is null || input.Version != 1 || input.NodeType != ActionNodeType.Swap || reference.Version != 1 || reference.Domain != Domain ||
            CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash || input.ActorUserId != reference.ActorUserId ||
            input.ActingTenantId != reference.ActingTenantId || reference.RequestKey != RequestKey(input.RunId, input.NodeId) ||
            reference.OperationId != SwapOperationIdentity.Operation(input.ActorUserId, input.ActingTenantId, SwapOperationIdentity.RequestKey(reference.RequestKey)))
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The saved swap envelope differs from its original frozen identity.");
        SwapNodeConfig? config;
        try { config = input.Config.Deserialize<SwapNodeConfig>(ConfigOptions); }
        catch (JsonException) { config = null; }
        if (config?.Quote is not { Network: not null, InputAsset: not null, OutputAsset: not null, Budget: not null } request)
            return Observation(ActionExternalOperationStatus.Failed, reference, "A typed exact-input swap quote request is required.");
        var chain = request.Network.ChainType;
        var loaded = await executions.GetByRunAndNodeAsync(input.RunId, input.NodeId, ct);
        if (loaded.IsError || loaded.Result is not { } execution || execution.ExternalOperation != reference ||
            execution.FrozenInput != canonicalInput || execution.SigningAuthorityJson is null)
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The exact admitted swap signing execution is unavailable.");
        var witness = JsonSerializer.Deserialize<WorkflowSigningWitness>(execution.SigningAuthorityJson)!;
        if (witness.Action.RunId != input.RunId || witness.Action.NodeId != input.NodeId || witness.Action.IntentHash != reference.IntentHash ||
            witness.Credential.ActorUserId != input.ActorUserId || witness.Credential.TenantId != input.ActingTenantId)
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The frozen workflow signing witness belongs to another node.");
        var operationId = reference.OperationId!;
        var row = await operations.GetAsync(operationId, ct);
        if (row is null)
        {
            if (modules.Resolve(request.Network) is null)
                return Observation(ActionExternalOperationStatus.Failed, reference,
                    "No reviewed DEX module is enabled for this network; nothing was quoted or admitted.", chain);
            var quoted = await quotes.QuoteAsync(witness.Credential, request, ct);
            if (quoted.IsError || quoted.Result is null)
                return Observation(quoted.Code is "Forbidden" or AzoaErrorCodes.InvalidRequest ? ActionExternalOperationStatus.Failed
                    : ActionExternalOperationStatus.Pending, reference, quoted.Message, chain);
            if (quoted.Result.Unavailable || quoted.Result.Quote is not { } view)
                return Observation(ActionExternalOperationStatus.Pending, reference,
                    "No current quote is available: " + (quoted.Result.Reason ?? "no reviewed liquidity."), chain);
            var started = await swaps.StartAsync(witness.Credential, new(view.QuoteId, view.QuoteHash, reference.RequestKey, true), witness, ct);
            row = await operations.GetAsync(operationId, ct);
            if (row is null) return Observation(ActionExternalOperationStatus.Pending, reference, started.Message, chain);
        }
        if (row.ActorUserId != input.ActorUserId.ToString("N") || row.TenantId != (input.ActingTenantId?.ToString("N") ?? "") ||
            row.ActionRunId != input.RunId.ToString("N") || row.ActionNodeId != input.NodeId.ToString("N") || row.ActionIntentHash != reference.IntentHash)
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The saved swap belongs to another frozen request.");
        var intent = JsonSerializer.Deserialize<SwapIntent>(row.IntentJson)!;
        if (!SameFrozenRequest(intent.Terms, request))
            return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The admitted swap changed the frozen request terms.");
        for (var step = 0; step < 6; step++)
        {
            if (row.SettlementReceiptJson is not null) return Settled(reference, intent, row.SettlementReceiptJson);
            if (row.State == "Cancelled")
            {
                // Idempotent: re-hands an unsent stage's nonce back if a crash interrupted the original cancellation.
                if (row.CurrentStageId is not null) await swaps.CancelUnsentAsync(operationId, row.Version, witness.Credential, ct);
                return Observation(ActionExternalOperationStatus.Cancelled, reference,
                    "The swap was cancelled without economic settlement; any confirmed prerequisite stays a disclosed retained effect.", chain);
            }
            if (row.State == "NeedsOperator")
                return Observation(ActionExternalOperationStatus.Ambiguous, reference, "The swap needs operator reconciliation of its saved stage.", chain);
            if (row.CleanupStarted)
                return Observation(ActionExternalOperationStatus.Failed, reference,
                    "The swap ended without economic settlement; retained prerequisites follow their separate cleanup.", chain);
            var stage = row.CurrentStageId is { } stageId ? await operations.GetStageAsync(stageId, ct) : null;
            AZOAResult<SwapOperationView> advanced;
            if (row.State is "Admitted" or "StageResolved")
                advanced = await swaps.PrepareAsync(operationId, row.Version, witness.Credential, witness, ct);
            else if (row.State == "Preparing" && stage is { State: "Planned" or "Prepared" })
            {
                var command = new SwapStageCommand(row.Version, SurrealRecordGuid.BareId(stage.Id), stage.Version);
                advanced = stage.State == "Planned" ? await swaps.SignWithCustodyAsync(operationId, command, witness.Credential, witness, ct)
                    : await swaps.SubmitAsync(operationId, command, witness.Credential, witness, ct);
            }
            else if (row.State == "Pending")
            {
                advanced = await swaps.RefreshAsync(operationId, witness.Credential, ct);
                var refreshed = await operations.GetAsync(operationId, ct);
                if (refreshed is null || refreshed.State == "Pending")
                    return Observation(ActionExternalOperationStatus.Pending, reference, advanced.Result?.Detail ?? "Awaiting finalized settlement.", chain);
            }
            else return Observation(ActionExternalOperationStatus.Pending, reference, $"The swap is {row.State}.", chain);
            if (advanced.IsError && advanced.Code == DexResultCodes.Unsettleable)
            {
                // Nothing further will be sent: withdraw any unsent plan (and its provider reservation) before failing the node.
                if (await operations.GetAsync(operationId, ct) is { State: "Admitted" or "Preparing" } unsent)
                    await swaps.CancelUnsentAsync(operationId, unsent.Version, witness.Credential, ct);
                return Observation(ActionExternalOperationStatus.Failed, reference,
                    advanced.Message + " Any confirmed prerequisite stays a disclosed retained effect with its own cleanup.", chain);
            }
            if (advanced.IsError) return Observation(ActionExternalOperationStatus.Pending, reference, advanced.Message, chain);
            row = await operations.GetAsync(operationId, ct) ?? row;
        }
        return Observation(ActionExternalOperationStatus.Pending, reference, $"The swap is {row.State}.", chain);
    }

    private static bool SameFrozenRequest(DexQuoteTerms terms, DexQuoteRequest request)
        => terms.Owner.WalletId == request.WalletId && terms.Network == request.Network && terms.GrossInput == request.GrossInput &&
            terms.SlippageBps == request.SlippageBps && terms.Budget == request.Budget &&
            terms.InputAsset.Id == request.InputAsset.Id && terms.InputAsset.Decimals == request.InputAsset.Decimals &&
            terms.InputAsset.Native == request.InputAsset.Native && terms.OutputAsset.Id == request.OutputAsset.Id &&
            terms.OutputAsset.Decimals == request.OutputAsset.Decimals && terms.OutputAsset.Native == request.OutputAsset.Native;

    private static ActionExternalOperationObservation Settled(ActionExternalOperationReference reference, SwapIntent intent, string receiptJson)
    {
        var receipt = JsonSerializer.Deserialize<SwapSettlementReceipt>(receiptJson)!;
        var transaction = receipt.TransactionIds.LastOrDefault() ?? "";
        var output = JsonSerializer.Serialize(new SwapActionOutput(receipt.OperationId, intent.Terms.Network.ChainType, intent.Terms.Network.Network,
            intent.Terms.Provider, intent.Terms.InputAsset.Id, intent.Terms.OutputAsset.Id, receipt.ActualInput, receipt.ActualOutput,
            receipt.PlatformFeePaid, receipt.TransactionIds, transaction, receipt.RetainedEffects), ActionNodeJson.Options);
        return new(ActionExternalOperationStatus.Succeeded, reference, output, TransactionId: transaction, ChainType: intent.Terms.Network.ChainType);
    }

    private static ActionExternalOperationObservation Observation(ActionExternalOperationStatus status,
        ActionExternalOperationReference reference, string detail, string? chain = null)
        => new(status, reference, Detail: detail, ChainType: chain);
}
