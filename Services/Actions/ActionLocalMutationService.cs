using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using ActionLocalReceipt = AZOA.WebAPI.Persistence.SurrealDb.Models.ActionLocalReceipt;
using AZOA.WebAPI.Services.Actions.Handlers;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Plans manager mutations against overlays and commits their effects with an immutable receipt.</summary>
public sealed class ActionLocalMutationService(IServiceProvider services, IHolonStore holons, IWalletStore wallets,
    IActionLocalMutationStore mutations, IActionNodeExecutionStore executions, ISurrealExecutor executor)
    : IActionExternalOperationResolver
{
    private static readonly IReadOnlyDictionary<ActionNodeType, Type> HandlerTypes = new Dictionary<ActionNodeType, Type>
    {
        [ActionNodeType.HolonCreate] = typeof(HolonCreateNodeHandler),
        [ActionNodeType.HolonUpdate] = typeof(HolonUpdateNodeHandler),
        [ActionNodeType.HolonDelete] = typeof(HolonDeleteNodeHandler),
        [ActionNodeType.HolonInteract] = typeof(HolonInteractNodeHandler),
        [ActionNodeType.HolonPropagate] = typeof(HolonPropagateNodeHandler),
        [ActionNodeType.HolonClone] = typeof(HolonCloneNodeHandler),
        [ActionNodeType.HolonMoveSubtree] = typeof(HolonMoveSubtreeNodeHandler),
        [ActionNodeType.WalletCreate] = typeof(WalletCreateNodeHandler),
        [ActionNodeType.WalletUpdate] = typeof(WalletUpdateNodeHandler),
        [ActionNodeType.WalletDelete] = typeof(WalletDeleteNodeHandler),
        [ActionNodeType.WalletSetDefault] = typeof(WalletSetDefaultNodeHandler),
        [ActionNodeType.LoanOpen] = typeof(LoanOpenNodeHandler),
        [ActionNodeType.LoanRepaymentRecord] = typeof(LoanRepaymentRecordNodeHandler)
    };

    /// <inheritdoc/>
    public string Domain => "local";

    public static bool Supports(ActionNodeType type) => HandlerTypes.ContainsKey(type);

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
    {
        var key = $"action:{context.RunId:N}:{context.NodeId:N}:local";
        var id = CanonicalJson.Hash(new { Domain = "local-v1", context.RunId, context.NodeId,
            context.ActingUserId, context.ActingTenantId })[..32];
        return new(1, "local", key, id, intentHash, context.ActingUserId, context.ActingTenantId);
    }

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var admitted = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (admitted.IsError || admitted.Result?.ExternalOperation is not { } reference
            || admitted.Result.FrozenInput is not { } frozen || reference.Domain != Domain)
            return ActionNodeHandlerResult.Invalid("Local effects require durable workflow admission.");
        var observation = await ObserveAsync(reference, frozen, ct);
        return observation.Status == ActionExternalOperationStatus.Succeeded
            ? ActionNodeHandlerResult.Ok(observation.Output)
            : ActionNodeHandlerResult.Pending(observation);
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return Refuse(reference, "Invalid local command envelope.");
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (input is null || input.Version != 1 || input.ActorUserId != reference.ActorUserId
            || input.ActingTenantId != reference.ActingTenantId || !HandlerTypes.TryGetValue(input.NodeType, out var handlerType))
            return Refuse(reference, "Invalid local command scope.");
        var action = new ActionDefinition { Nodes = [new ActionNode
            { Id = input.NodeId, NodeType = input.NodeType, Config = input.Config.GetRawText() }] };
        var context = new ActionNodeExecutionContext(input.RunId, input.NodeId, action, input.ActorUserId,
            input.Upstream, input.ActingTenantId, input.AllExecutions);
        if (Reference(context, reference.IntentHash) != reference) return Refuse(reference, "Local command correlation changed.");
        var expected = new ActionLocalReceipt
        {
            Id = reference.OperationId!, RunId = input.RunId.ToString("N"), NodeId = input.NodeId.ToString("N"),
            ActorId = input.ActorUserId.ToString("N"), TenantId = input.ActingTenantId?.ToString("N"),
            IntentHash = reference.IntentHash, CreatedAt = DateTimeOffset.UtcNow
        };
        var previous = await mutations.GetReceiptAsync(expected.Id, ct);
        if (previous is not null) return Receipt(reference, previous, expected);

        var policyVersion = await NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);
        var plan = new ActionLocalMutationPlan();
        var stagedHolons = new ActionLocalHolonOverlay(holons, plan);
        var stagedWallets = new ActionLocalWalletOverlay(wallets, plan);
        Func<Guid> nextId = new ActionLocalEntityIds(expected.Id).Next;
        var walletNode = input.NodeType is ActionNodeType.WalletCreate or ActionNodeType.WalletUpdate
            or ActionNodeType.WalletDelete or ActionNodeType.WalletSetDefault;
        object manager = walletNode
            ? ActivatorUtilities.CreateInstance<WalletManager>(services, stagedWallets, stagedHolons, nextId)
            : ActivatorUtilities.CreateInstance<HolonManager>(services, stagedHolons, nextId);
        var handler = (IActionNodeHandler)ActivatorUtilities.CreateInstance(services, handlerType, manager);
        var result = await handler.HandleAsync(context, ct);
        if (result.IsError || result.PendingOperation is not null || !plan.IsWithinBounds())
        {
            if ((result.Retriable || plan.Refusal is not null || result.PendingOperation is not null)
                && !plan.RefusalIsDeterministic)
                return Refuse(reference, plan.Refusal ?? result.Message ?? "Local planning requires recovery.");
            expected.IsError = true;
            expected.Message = plan.Refusal ?? result.Message ?? "Local mutation planning did not complete.";
            expected.OutputJson = "null";
            var failed = await mutations.CommitAsync(new ActionLocalMutationPlan(), expected, policyVersion, walletNode, ct);
            return failed.IsError || failed.Result is null ? Refuse(reference, failed.Message)
                : Receipt(reference, failed.Result, expected);
        }
        expected.OutputJson = result.Output ?? "null";
        var committed = await mutations.CommitAsync(plan, expected, policyVersion, walletNode, ct);
        return committed.IsError || committed.Result is null ? Refuse(reference, committed.Message)
            : Receipt(reference, committed.Result, expected);
    }

    private static ActionExternalOperationObservation Receipt(ActionExternalOperationReference reference,
        ActionLocalReceipt saved, ActionLocalReceipt expected)
        => saved.RunId == expected.RunId && saved.NodeId == expected.NodeId && saved.ActorId == expected.ActorId
            && saved.TenantId == expected.TenantId && saved.IntentHash == expected.IntentHash && saved.Id == expected.Id
            ? new(saved.IsError ? ActionExternalOperationStatus.Failed : ActionExternalOperationStatus.Succeeded,
                reference, saved.OutputJson, saved.Message)
            : Refuse(reference, "Local receipt conflicts with the admitted command.");

    private static ActionExternalOperationObservation Refuse(ActionExternalOperationReference reference, string? detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
