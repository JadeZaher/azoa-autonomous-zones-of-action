using System.Text.Json;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Repeats only a closed catalog of authorized reads and pure computations after a lost result.</summary>
public sealed class ActionReadRecovery(IServiceProvider services, IActionRunStore runs) : IActionExternalOperationResolver
{
    private static readonly IReadOnlyDictionary<ActionNodeType, Type> Handlers = new Dictionary<ActionNodeType, Type>
    {
        [ActionNodeType.Condition] = typeof(ConditionNodeHandler), [ActionNodeType.ComposeOutputs] = typeof(ComposeOutputsNodeHandler),
        [ActionNodeType.GateCheck] = typeof(GateCheckNodeHandler), [ActionNodeType.Search] = typeof(SearchNodeHandler),
        [ActionNodeType.HolonQuery] = typeof(HolonQueryNodeHandler), [ActionNodeType.HolonGet] = typeof(HolonGetNodeHandler),
        [ActionNodeType.HolonGetAncestors] = typeof(HolonGetAncestorsNodeHandler), [ActionNodeType.HolonGetChildren] = typeof(HolonGetChildrenNodeHandler),
        [ActionNodeType.HolonGetDescendants] = typeof(HolonGetDescendantsNodeHandler), [ActionNodeType.HolonGetPeers] = typeof(HolonGetPeersNodeHandler),
        [ActionNodeType.HolonCompose] = typeof(HolonComposeNodeHandler), [ActionNodeType.WalletQuery] = typeof(WalletQueryNodeHandler),
        [ActionNodeType.WalletGet] = typeof(WalletGetNodeHandler), [ActionNodeType.WalletGetPortfolio] = typeof(WalletGetPortfolioNodeHandler),
        [ActionNodeType.NftQuery] = typeof(NftQueryNodeHandler), [ActionNodeType.NftGet] = typeof(NftGetNodeHandler),
        [ActionNodeType.NftGetMetadata] = typeof(NftGetMetadataNodeHandler), [ActionNodeType.UserNFTGetComposite] = typeof(UserNFTGetCompositeNodeHandler),
        [ActionNodeType.LoanBalance] = typeof(LoanBalanceNodeHandler),
    };

    /// <inheritdoc/>
    public string Domain => "read";
    public static bool Supports(ActionNodeType type) => Handlers.ContainsKey(type);
    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash) =>
        new(1, "read", $"action:{context.RunId:N}:{context.NodeId:N}:read", context.NodeId.ToString("N"), intentHash,
            context.ActingUserId, context.ActingTenantId);

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return Refuse(reference);
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (input is null || input.Version != 1 || !Handlers.TryGetValue(input.NodeType, out var type)
            || input.ActorUserId != reference.ActorUserId || input.ActingTenantId != reference.ActingTenantId) return Refuse(reference);
        var run = await runs.GetByIdAsync(input.RunId, ct);
        if (run.IsError || run.Result is null || run.Result.UserId != input.ActorUserId || run.Result.ActingTenantId != input.ActingTenantId)
            return Refuse(reference);
        var graph = ActionRunSnapshot.Read(run.Result);
        var node = graph.Nodes.SingleOrDefault(item => item.Id == input.NodeId);
        if (node is null || node.NodeType != input.NodeType) return Refuse(reference);
        node.Config = input.Config.GetRawText();
        var context = new ActionNodeExecutionContext(input.RunId, input.NodeId, graph, input.ActorUserId,
            input.Upstream, input.ActingTenantId, input.AllExecutions);
        if (Reference(context, reference.IntentHash) != reference) return Refuse(reference);
        var handler = (IActionNodeHandler)ActivatorUtilities.CreateInstance(services, type);
        var outcome = await handler.HandleAsync(context, ct);
        if (outcome.PendingOperation is not null)
            return new(ActionExternalOperationStatus.Ambiguous, reference, Detail: "Read handler returned an unexpected external operation.");
        if (outcome.IsError && outcome.Retriable)
            return new(ActionExternalOperationStatus.Pending, reference, Detail: outcome.Message);
        return new(outcome.IsError ? ActionExternalOperationStatus.Failed : ActionExternalOperationStatus.Succeeded,
            reference, outcome.Output, outcome.Message);
    }

    private static ActionExternalOperationObservation Refuse(ActionExternalOperationReference reference)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: "Read recovery does not match its frozen run and caller.");
}

/// <summary>Preserves the original read handler while declaring its safe recovery boundary.</summary>
public sealed class ActionReadHandler(IActionNodeHandler inner) : IRecoverableActionNodeHandler
{
    /// <inheritdoc/>
    public ActionNodeType NodeType => inner.NodeType;
    /// <inheritdoc/>
    public bool RequiresChainCapability => inner.RequiresChainCapability;
    /// <inheritdoc/>
    public string? ResolveSettlementRailId(ActionNode node) => inner.ResolveSettlementRailId(node);
    /// <inheritdoc/>
    public ActionExternalOperationReference CreateReference(ActionNodeExecutionContext context, string intentHash)
        => ActionReadRecovery.Reference(context, intentHash);
    /// <inheritdoc/>
    public Task<ActionNodeHandlerResult> HandleAsync(ActionNodeExecutionContext context, CancellationToken ct = default)
        => inner.HandleAsync(context, ct);
}
