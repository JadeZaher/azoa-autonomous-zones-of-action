using System.Text.Json;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Services.Hold;
using AZOA.WebAPI.Services.Reserve;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Recovers original workflow custody commands without obtaining participant signatures.</summary>
public sealed partial class ActionHoldOperationService(IHoldCommandStore holds, HoldCommandService commands,
    HoldRailExecutor rail, IActionNodeExecutionStore executions, IActionRunStore runs,
    IPlatformReserveLedgerStore reserve, IBlockchainProviderFactory providers)
    : IActionExternalOperationResolver
{
    /// <inheritdoc/>
    public string Domain => "hold";

    public static bool Supports(ActionNodeType type) => type is ActionNodeType.Hold or ActionNodeType.Release
        or ActionNodeType.HoldRefund or ActionNodeType.ReleaseIntoHold;

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string hash)
    {
        var key = $"action:{context.RunId:N}:{context.NodeId:N}:hold";
        return new(1, "hold", key, HoldCommandIdentity.Operation(context.ActingUserId, context.ActingTenantId, key),
            hash, context.ActingUserId, context.ActingTenantId);
    }

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var loaded = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (loaded.IsError || loaded.Result?.ExternalOperation is not { } reference || loaded.Result.FrozenInput is not { } frozen
            || Reference(context, reference.IntentHash) != reference)
            return ActionNodeHandlerResult.Invalid("Hold effects require original durable workflow admission.");
        var observed = await ObserveAsync(reference, frozen, ct);
        return observed.Status == ActionExternalOperationStatus.Succeeded
            ? ActionNodeHandlerResult.Ok(observed.Output, txHash: observed.TransactionId, chainType: observed.ChainType)
            : ActionNodeHandlerResult.Pending(observed);
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (input is null || input.Version != 1 || !Supports(input.NodeType) || reference.Version != 1 || reference.Domain != Domain
            || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash
            || input.ActorUserId != reference.ActorUserId || input.ActingTenantId != reference.ActingTenantId
            || reference.RequestKey != $"action:{input.RunId:N}:{input.NodeId:N}:hold"
            || reference.OperationId != HoldCommandIdentity.Operation(input.ActorUserId, input.ActingTenantId, reference.RequestKey))
            return Unknown(reference, "The saved Hold envelope differs from its original identity.");
        var row = await holds.GetCommandAsync(reference.OperationId, ct);
        if (row is null)
        {
            var loaded = await executions.GetByRunAndNodeAsync(input.RunId, input.NodeId, ct);
            if (loaded.IsError || loaded.Result is not { } execution || execution.ExternalOperation != reference
                || execution.FrozenInput != canonicalInput)
                return Unknown(reference, "The exact admitted Hold signing execution is unavailable.");
            if (execution.State == ActionNodeState.Failed && execution.RecoveryState == "HoldAdmissionRejected")
                return new(ActionExternalOperationStatus.Failed, reference, Detail: execution.Error);
            if (ValidationError(input) is { } invalid)
            {
                if (execution.State != ActionNodeState.Running) return Unknown(reference, "The original rejection execution changed.");
                var rejected = execution.Clone();
                rejected.State = ActionNodeState.Failed; rejected.RecoveryState = "HoldAdmissionRejected";
                rejected.Error = invalid; rejected.EndedAt = DateTime.UtcNow;
                var recorded = await executions.UpdateAsync(rejected, ActionNodeState.Running, ct);
                return recorded.IsError ? new(ActionExternalOperationStatus.Pending, reference, Detail: "The original rejection is being reconciled.")
                    : new(ActionExternalOperationStatus.Failed, reference, Detail: invalid);
            }
            if (execution.SigningAuthorityJson is null) return Unknown(reference, "The original Hold signing witness is unavailable.");
            var witness = JsonSerializer.Deserialize<WorkflowSigningWitness>(execution.SigningAuthorityJson)!;
            var planned = await PlanAsync(input, reference, ct);
            if (planned.Plan is null) return new(ActionExternalOperationStatus.Pending, reference,
                Detail: planned.Error ?? "The original Hold source needs reconciliation.");
            var result = await commands.AdmitWorkflowAsync(planned.Plan.Intent, witness, execution.Id,
                planned.Plan.RecipientWalletId, planned.Plan.Mandate, ct);
            row = await holds.GetCommandAsync(reference.OperationId, ct);
            if (row is null) return new(ActionExternalOperationStatus.Pending, reference, Detail: result.Message);
        }
        var original = JsonSerializer.Deserialize<HoldCommandAuthority>(row.AuthorityJson);
        var intent = JsonSerializer.Deserialize<HoldCommandIntent>(row.IntentJson);
        if (original?.Workflow?.Action is not { } source || source.RunId != input.RunId || source.NodeId != input.NodeId
            || source.ActorUserId != input.ActorUserId || source.ActingTenantId != input.ActingTenantId
            || source.IntentHash != reference.IntentHash || intent is null || row.IntentHash != CanonicalJson.Hash(intent)
            || intent.RequestKey != reference.RequestKey || intent.ActorUserId != input.ActorUserId || intent.TenantId != input.ActingTenantId)
            return Unknown(reference, "The custody command conflicts with its original frozen workflow.");
        if (row.RailPlanJson is not null && row.State != "Completed")
        {
            var advanced = await rail.AdvanceAsync(row, false, ct);
            if (advanced.IsError || advanced.Result is null) return Unknown(reference, advanced.Message);
            row = advanced.Result;
        }
        if (row.State == "Cancelled") return new(ActionExternalOperationStatus.Cancelled, reference, Detail: row.Detail);
        if (row.State != "Completed") return new(ActionExternalOperationStatus.Pending, reference,
            Detail: $"Hold command {row.Id} is {row.State}; any new consent or participant signature requires its recovery controls.", ChainType: intent.Asset.ChainType);
        var receipt = await holds.GetReceiptAsync(row.Id, ct);
        if (receipt is null || receipt.IntentHash != row.IntentHash || receipt.ActorUserId != row.ActorUserId || receipt.TenantId != row.TenantId)
            return Unknown(reference, "Completed Hold accounting has no matching immutable receipt.");
        var view = JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson)!;
        if (view.OperationId != row.Id || view.State != "Completed" || view.Asset != intent.Asset)
            return Unknown(reference, "The completed Hold receipt differs from the original command.");
        return new(ActionExternalOperationStatus.Succeeded, reference, Output(view, intent), TransactionId: view.TransactionId,
            ChainType: intent.Asset.ChainType);
    }

    private static string Output(HoldCommandView view, HoldCommandIntent intent)
        => HoldNodeResult.Serialize(HoldNodeResult.From(view, intent));

    private static ActionExternalOperationObservation Unknown(ActionExternalOperationReference reference, string reason)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: reason);
}
