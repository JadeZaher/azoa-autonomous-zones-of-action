using System.Text.Json;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Recovers fungible issuance from the frozen workflow intent without accepting costs or preparing signatures.</summary>
public sealed class ActionFungibleCreationService(IFungibleTokenManager tokens, IFungibleCreationService lifecycle,
    IActionNodeExecutionStore executions) : IActionExternalOperationResolver
{
    /// <inheritdoc/>
    public string Domain => "fungible-creation";

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
        => Reference(context.RunId, context.NodeId, context.ActingUserId, context.ActingTenantId, intentHash);

    private static ActionExternalOperationReference Reference(Guid run, Guid node, Guid actor, Guid? tenant, string hash)
    {
        var key = $"action:{run:N}:{node:N}:fungible";
        return new(1, "fungible-creation", key, FungibleCreationIdentity.OperationId(actor, tenant, key), hash, actor, tenant);
    }

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var execution = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (execution.IsError || execution.Result?.ExternalOperation is not { } reference
            || execution.Result.FrozenInput is not { } input || Reference(context, reference.IntentHash) != reference)
            return ActionNodeHandlerResult.Invalid("Fungible creation requires durable workflow admission.");
        return ActionNodeHandlerResult.Pending(await ObserveAsync(reference, input, ct));
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        if (reference.Version != 1 || reference.Domain != Domain || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash)
            return Refuse(reference, "Fungible recovery envelope is invalid.");
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (input is null || input.Version != 1 || input.NodeType != ActionNodeType.FungibleTokenCreate
            || Reference(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId, reference.IntentHash) != reference)
            return Refuse(reference, "Fungible recovery identity conflicts with its frozen input.");
        if (!ActionNodeConfig.TryDeserialize<FungibleTokenCreateNodeConfig>(input.Config.GetRawText(), nameof(ActionNodeType.FungibleTokenCreate), out var cfg, out var error))
            return new(ActionExternalOperationStatus.Failed, reference, Detail: error);
        var admitted = await tokens.CreateAsync(input.ActorUserId, new FungibleTokenCreateRequest
        {
            Name = cfg.Name, UnitName = cfg.UnitName, ChainType = cfg.ChainType, Network = cfg.Network,
            Total = cfg.Total, Decimals = cfg.Decimals, HolonId = cfg.HolonId,
            ActionAuthority = new(input.RunId, input.NodeId, input.ActorUserId, input.ActingTenantId, reference.IntentHash),
        }, input.ActorUserId, reference.RequestKey, input.RunId.ToString("N"), input.ActingTenantId);
        if (admitted.IsError || admitted.Result?.Operation is null) return Refuse(reference, admitted.Message);
        var observed = await lifecycle.RefreshAsync(reference.OperationId!, input.ActorUserId, input.ActingTenantId, ct);
        if (observed.IsError || observed.Result is null) return Refuse(reference, observed.Message);
        var view = observed.Result;
        if (view.State == "Cancelled") return new(ActionExternalOperationStatus.Cancelled, reference, Detail: view.Detail);
        if (view.State == "FailedNoEffect") return new(ActionExternalOperationStatus.Pending, reference,
            Detail: "Authoritative chain history proves this generation had no effect. The original participant can cancel or authorize a new generation and quote.");
        if (view.State == "AttachmentPending") return new(ActionExternalOperationStatus.Pending, reference,
            Detail: "Asset issuance is finalized and accounted for. The requested Holon's current owner must accept its exact attachment before this action completes.",
            TransactionId: view.TransactionId, ChainType: view.ChainType);
        if (view.State != "Completed") return new(ActionExternalOperationStatus.Pending, reference,
            Detail: $"Creation {view.OperationId} is {view.State}; participant cost acceptance and fresh signing use its recovery controls.",
            TransactionId: view.TransactionId, ChainType: view.ChainType);
        if (string.IsNullOrWhiteSpace(view.AssetId) || string.IsNullOrWhiteSpace(view.TransactionId))
            return Refuse(reference, "Completed creation lacks its original finalized asset evidence.");
        var result = AZOAResult<FungibleTokenResult>.Success(new()
        {
            UserId = input.ActorUserId, WalletId = view.WalletId, WalletAddress = view.WalletAddress,
            WalletProvisioned = view.WalletProvisioned, AssetId = view.AssetId, Operation = view, Replayed = true,
        });
        return new(ActionExternalOperationStatus.Succeeded, reference, ActionNodeOutputProjection.SerializeFungible(result),
            TransactionId: view.TransactionId, ChainType: view.ChainType);
    }

    private static ActionExternalOperationObservation Refuse(ActionExternalOperationReference reference, string? detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
