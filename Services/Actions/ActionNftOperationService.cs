using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Actions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using NftRow = AZOA.WebAPI.Persistence.SurrealDb.Models.NftLifecycle;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>Correlates frozen NFT actions with their durable, participant-authorized lifecycle.</summary>
public sealed class ActionNftOperationService(INftManager nfts, ICredentialManager credentials,
    INftLifecycleStore store, INftLifecycleReconciler reconciler, IActionNodeExecutionStore executions,
    INftLifecycleService lifecycle, ActionNftHolonLinkService? links = null)
    : IActionExternalOperationResolver
{
    /// <inheritdoc/>
    public string Domain => "nft";

    public static bool Supports(ActionNodeType type) => type is ActionNodeType.NftMint or ActionNodeType.NftTransfer
        or ActionNodeType.NftBurn or ActionNodeType.CredentialRevoke or ActionNodeType.Grant or ActionNodeType.Transfer or ActionNodeType.Refund;

    public static ActionExternalOperationReference Reference(ActionNodeExecutionContext context, string intentHash)
    {
        var key = $"action:{context.RunId:N}:{context.NodeId:N}:nft";
        return new(1, "nft", key, NftLifecycleIdentity.ForRequest(context.ActingUserId,
            Operation(context.Node.NodeType), key, context.ActingTenantId ?? Guid.Empty), intentHash,
            context.ActingUserId, context.ActingTenantId);
    }

    public async Task<ActionNodeHandlerResult> ExecuteAsync(ActionNodeExecutionContext context, CancellationToken ct)
    {
        var execution = await executions.GetByRunAndNodeAsync(context.RunId, context.NodeId, ct);
        if (execution.IsError || execution.Result?.ExternalOperation is not { } reference
            || execution.Result.FrozenInput is not { } frozen || Reference(context, reference.IntentHash) != reference)
            return ActionNodeHandlerResult.Invalid("NFT effects require durable workflow admission.");
        var input = Parse(reference, frozen);
        if (input is null) return ActionNodeHandlerResult.Invalid("NFT action intent is invalid.");
        if (ValidationError(input) is { } invalid)
            return ActionNodeHandlerResult.Pending(await RejectAsync(reference, input, invalid, ct));
        var prior = await store.GetAsync(reference.OperationId!, ct);
        if (prior is not null && !Matches(prior, input, reference.IntentHash))
            return ActionNodeHandlerResult.Pending(Ambiguous(reference, "NFT request identity conflicts with the frozen action."));
        if (prior is { State: "Failed", Stage: "AdmissionFailure" })
            return ActionNodeHandlerResult.Pending(new(ActionExternalOperationStatus.Failed, reference, Detail: prior.Detail));
        if (prior is null && NeedsWorkflowCostConsent(input))
            return ActionNodeHandlerResult.Pending(CostConsentPending(reference));
        var detail = NeedsWorkflowCostConsent(input) ? null
            : await DispatchAsync(input, reference.RequestKey, reference.IntentHash, admissionOnly: false, ct);
        var observation = await ObserveCoreAsync(reference, input, admitMissing: false, ct);
        if (observation.Status == ActionExternalOperationStatus.Ambiguous && detail is not null)
            observation = observation with { Detail = detail };
        return observation.Status == ActionExternalOperationStatus.Succeeded
            ? ActionNodeHandlerResult.Ok(observation.Output, txHash: observation.TransactionId, chainType: observation.ChainType)
            : ActionNodeHandlerResult.Pending(observation);
    }

    /// <inheritdoc/>
    public async Task<ActionExternalOperationObservation> ObserveAsync(ActionExternalOperationReference reference,
        string canonicalInput, CancellationToken ct = default)
    {
        var input = Parse(reference, canonicalInput);
        if (input is null) return Ambiguous(reference, "NFT action envelope is invalid.");
        return ValidationError(input) is { } invalid ? await RejectAsync(reference, input, invalid, ct)
            : await ObserveCoreAsync(reference, input, admitMissing: true, ct);
    }

    private async Task<ActionExternalOperationObservation> ObserveCoreAsync(ActionExternalOperationReference reference,
        ActionFrozenNodeInput input, bool admitMissing, CancellationToken ct)
    {
        var row = await store.GetAsync(reference.OperationId!, ct);
        if (row is null && NeedsWorkflowCostConsent(input)) return CostConsentPending(reference);
        if (row is null && admitMissing)
        {
            var detail = await DispatchAsync(input, reference.RequestKey, reference.IntentHash, admissionOnly: true, ct);
            row = await store.GetAsync(reference.OperationId!, ct);
            if (row is null) return Ambiguous(reference, detail ?? "NFT admission requires current participant authority.");
        }
        if (row is null) return Ambiguous(reference, "NFT admission has no durable receipt; the original request may still arrive.");
        if (!Matches(row, input, reference.IntentHash)) return Ambiguous(reference, "NFT lifecycle differs from the frozen action intent.");
        if (row.State == "Failed") return new(ActionExternalOperationStatus.Failed, reference, Detail: row.Detail);
        if (row.State == "Cancelled") return new(ActionExternalOperationStatus.Cancelled, reference, Detail: "NFT request was cancelled.");
        var observed = await reconciler.ReconcileAsync(row.Id, ct);
        if (observed.IsError || observed.Result is null) return Ambiguous(reference, observed.Message);
        row = await store.GetAsync(row.Id, ct) ?? throw new InvalidOperationException("Admitted NFT lifecycle disappeared.");
        if (!Matches(row, input, reference.IntentHash)) return Ambiguous(reference, "NFT lifecycle identity changed during observation.");
        if (row.State == "Cancelled") return new(ActionExternalOperationStatus.Cancelled, reference, Detail: "NFT request was cancelled.");
        if (row.State != "Completed") return new(ActionExternalOperationStatus.Pending, reference,
            Detail: $"NFT lifecycle {row.Id} is {row.State}; any new signature requires the participant's lifecycle controls.", ChainType: row.ChainType);
        if (string.IsNullOrWhiteSpace(row.FinalTransactionId)) return Ambiguous(reference, "Completed NFT lifecycle has no final transaction evidence.");
        if (input.NodeType == ActionNodeType.Grant && Config<GrantNodeConfig>(input, out var grant) && grant.HolonId is not null)
        {
            if (links is null) return Ambiguous(reference, "Confirmed grant attachment recovery is unavailable.");
            if (await links.LinkAsync(reference, input, row, ct) is { } attachment) return attachment;
        }
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var view = new NftLifecycleView(row.Id, Guid.Parse(row.NftId), row.State, row.Stage, row.AssetId,
            row.FinalTransactionId, row.Detail, row.Operation);
        var output = input.NodeType == ActionNodeType.CredentialRevoke
            ? JsonSerializer.Serialize(new CredentialRevokeResult
            {
                AssetId = row.AssetId!, CredentialId = Guid.Parse(row.NftId), IssuerUserId = command.Actor,
                HolderUserId = command.SourceUserId ?? throw new InvalidOperationException("Revocation source identity is missing."),
                HolderAddress = command.SourceAddress ?? throw new InvalidOperationException("Revocation source address is missing."),
                RecipientAddress = command.RecipientAddress, ChainType = row.ChainType, Status = OperationStatus.Completed,
                TxHash = row.FinalTransactionId, OperationId = Guid.Parse(row.Id), LifecycleId = row.Id,
                LifecycleState = row.State, Replayed = true,
            }, ActionNodeJson.Options)
            : ActionNodeOutputProjection.SerializeOperation(NftLifecycleOperationProjection.Project(
                AZOAResult<NftLifecycleView>.Success(view), command.Actor, command.ActorWalletId, row.ChainType));
        return new(ActionExternalOperationStatus.Succeeded, reference, output, TransactionId: row.FinalTransactionId, ChainType: row.ChainType);
    }

    private async Task<string?> DispatchAsync(ActionFrozenNodeInput input, string key, string intentHash, bool admissionOnly, CancellationToken ct)
    {
        var authority = Authority(input, intentHash);
        var request = new AZOARequest { IdempotencyKey = key, NftAdmissionOnly = admissionOnly, ActionAuthority = authority };
        switch (input.NodeType)
        {
            case ActionNodeType.NftMint:
            case ActionNodeType.Grant:
                if (!MintConfig(input, out var mint)) return "NFT mint configuration is invalid.";
                return (await nfts.MintAsync(mint, input.ActorUserId, request, input.ActingTenantId)).Message;
            case ActionNodeType.NftTransfer:
            case ActionNodeType.Transfer:
                if (!Config<NftTransferNodeConfig>(input, out var transfer)) return "NFT transfer configuration is invalid.";
                return (await nfts.TransferAsync(transfer.NftId, transfer.Request, input.ActorUserId, request, input.ActingTenantId)).Message;
            case ActionNodeType.Refund:
                if (!Config<RefundNodeConfig>(input, out var refund) || RefundSource(input, refund) is not { } source)
                    return "Refund requires one exact succeeded transfer earlier in the frozen run.";
                var refunded = admissionOnly
                    ? await lifecycle.AdmitRefundAsync(input.ActorUserId, source, key, ct, input.ActingTenantId ?? Guid.Empty, refund.Request.FeeAuthorization, authority)
                    : await lifecycle.RefundAsync(input.ActorUserId, source, key, ct, input.ActingTenantId ?? Guid.Empty, refund.Request.FeeAuthorization, authority);
                return refunded.Message;
            case ActionNodeType.NftBurn:
                if (!Config<NftBurnNodeConfig>(input, out var burn)) return "NFT burn configuration is invalid.";
                request.NftFeeAuthorization = burn.FeeAuthorization;
                return (await nfts.BurnAsync(burn.NftId, burn.WalletId, input.ActorUserId, request, input.ActingTenantId)).Message;
            case ActionNodeType.CredentialRevoke:
                if (!Config<CredentialRevokeNodeConfig>(input, out var revoke)) return "Credential revocation configuration is invalid.";
                return (await credentials.RevokeAsync(revoke.AssetId, new CredentialRevokeRequest
                {
                    ChainType = revoke.ChainType, Network = revoke.Network, ContractAddress = revoke.ContractAddress,
                    Reason = revoke.Reason, FeeAuthorization = revoke.FeeAuthorization, AdmissionOnly = admissionOnly,
                    ActionAuthority = authority,
                }, input.ActorUserId, key, input.ActingTenantId, ct)).Message;
            default: throw new InvalidOperationException("Unsupported NFT action.");
        }
    }

    private static ActionFrozenNodeInput? Parse(ActionExternalOperationReference reference, string canonicalInput)
    {
        if (reference.Version != 1 || reference.Domain != "nft" || CanonicalJson.HashCanonical(canonicalInput) != reference.IntentHash) return null;
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(canonicalInput);
        if (input is null || input.Version != 1 || !Supports(input.NodeType)
            || input.ActorUserId != reference.ActorUserId || input.ActingTenantId != reference.ActingTenantId) return null;
        var key = $"action:{input.RunId:N}:{input.NodeId:N}:nft";
        return reference.RequestKey == key && reference.OperationId == NftLifecycleIdentity.ForRequest(input.ActorUserId,
            Operation(input.NodeType), key, input.ActingTenantId ?? Guid.Empty) ? input : null;
    }

    private static bool Matches(NftRow row, ActionFrozenNodeInput input, string intentHash)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson);
        if (command is null || command.Actor != input.ActorUserId || command.ActingTenantId != (input.ActingTenantId ?? Guid.Empty)
            || row.ActorUserId != input.ActorUserId.ToString("N") || row.Operation != Operation(input.NodeType)) return false;
        if (row.Stage == "Cancellation" && row.State == "Cancelled") return true;
        if (command.ActionAuthority != Authority(input, intentHash)) return false;
        if (row.Stage == "AdmissionFailure" && row.State == "Failed") return true;
        return input.NodeType switch
        {
            ActionNodeType.NftMint or ActionNodeType.Grant => MintConfig(input, out var mint) && command.Recipient == input.ActorUserId
                && (CanonicalJson.Hash(command.Metadata) == CanonicalJson.Hash(NftLifecycleOperationProjection.MintModel(mint))
                    || (command.WorkflowCostAcceptance is { } acceptance && NftWorkflowCostAcceptanceIdentity.Matches(acceptance)
                        && acceptance.Reference.OperationId == row.Id && acceptance.Reference.IntentHash == intentHash
                        && NftProtocolCostContract.DraftHash(row.Id, command.Actor, command.Recipient, command.Metadata)
                            == NftProtocolCostContract.DraftHash(row.Id, command.Actor, command.Recipient, NftLifecycleOperationProjection.MintModel(mint)))),
            ActionNodeType.NftTransfer or ActionNodeType.Transfer => Config<NftTransferNodeConfig>(input, out var transfer) && transfer.Request is not null
                && row.NftId == transfer.NftId.ToString("N") && command.ActorWalletId == transfer.Request.WalletId
                && command.Recipient == transfer.Request.TargetUserId && command.FeeAuthorization == transfer.Request.FeeAuthorization,
            ActionNodeType.Refund => Config<RefundNodeConfig>(input, out var refund) && refund.Request is not null
                && row.NftId == refund.NftId.ToString("N") && command.SourceLifecycleId == RefundSource(input, refund)
                && command.FeeAuthorization == refund.Request.FeeAuthorization,
            ActionNodeType.NftBurn => Config<NftBurnNodeConfig>(input, out var burn) && row.NftId == burn.NftId.ToString("N")
                && command.ActorWalletId == burn.WalletId && command.FeeAuthorization == burn.FeeAuthorization,
            ActionNodeType.CredentialRevoke => Config<CredentialRevokeNodeConfig>(input, out var revoke)
                && row.AssetId == revoke.AssetId && string.Equals(row.ChainType, revoke.ChainType, StringComparison.OrdinalIgnoreCase)
                && (revoke.Network is null || string.Equals(row.Network, revoke.Network, StringComparison.OrdinalIgnoreCase))
                && (revoke.ContractAddress is null || ChainAddressIdentity.Equivalent(row.ChainType, row.ContractAddress, revoke.ContractAddress))
                && command.Reason == revoke.Reason && command.FeeAuthorization == revoke.FeeAuthorization,
            _ => false,
        };
    }

    private static bool Config<T>(ActionFrozenNodeInput input, out T result) where T : class, new()
        => ActionNodeConfig.TryDeserialize(input.Config.GetRawText(), input.NodeType.ToString(), out result, out _);

    private static ActionEffectAuthority Authority(ActionFrozenNodeInput input, string intentHash) => new(input.RunId, input.NodeId,
        input.ActorUserId, input.ActingTenantId, intentHash);

    private async Task<ActionExternalOperationObservation> RejectAsync(ActionExternalOperationReference reference,
        ActionFrozenNodeInput input, string detail, CancellationToken ct)
    {
        var command = new NftLifecycleCommand(input.ActorUserId, input.ActorUserId, Guid.Empty, Guid.Empty,
            string.Empty, string.Empty, new(), -1, input.ActingTenantId ?? Guid.Empty, ActionAuthority: Authority(input, reference.IntentHash));
        var json = JsonSerializer.Serialize(command);
        var candidate = new NftRow
        {
            Id = reference.OperationId!, ActorUserId = input.ActorUserId.ToString("N"), RecipientUserId = input.ActorUserId.ToString("N"),
            NftId = Guid.Empty.ToString("N"), Operation = Operation(input.NodeType), State = "Failed", Stage = "AdmissionFailure",
            RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json), Detail = detail,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        NftRow? claimed;
        try { claimed = await store.ClaimAsync(candidate, ct); }
        catch (ActionEffectAuthorityChangedException) { return Ambiguous(reference, "Workflow authority changed before rejection could be recorded."); }
        if (claimed is null || !Matches(claimed, input, reference.IntentHash)) return Ambiguous(reference, "NFT rejection conflicts with an existing request.");
        return claimed is { State: "Failed", Stage: "AdmissionFailure" }
            ? new(ActionExternalOperationStatus.Failed, reference, Detail: claimed.Detail)
            : await ObserveCoreAsync(reference, input, admitMissing: false, ct);
    }

    private static string? ValidationError(ActionFrozenNodeInput input) => input.NodeType switch
    {
        ActionNodeType.NftMint or ActionNodeType.Grant when !MintConfig(input, out var mint) || mint.WalletId == Guid.Empty
            || string.IsNullOrWhiteSpace(mint.ChainId) || string.IsNullOrWhiteSpace(mint.MetadataUri)
            || !string.IsNullOrWhiteSpace(mint.TokenId) => "NFT mint requires a wallet, chain, metadata URI and no imported token id.",
        ActionNodeType.NftTransfer or ActionNodeType.Transfer when !Config<NftTransferNodeConfig>(input, out var move) || move.NftId == Guid.Empty
            || move.Request is null || move.Request.WalletId == Guid.Empty || move.Request.TargetUserId == Guid.Empty
            => "NFT transfer requires an asset, signing wallet and recipient.",
        ActionNodeType.Refund when !Config<RefundNodeConfig>(input, out var refund) || refund.Request is null
            || refund.NftId == Guid.Empty || RefundSource(input, refund) is null
            => "Refund requires one exact succeeded transfer earlier in the frozen run; choose TransferNodeId when more than one matches.",
        ActionNodeType.NftBurn when !Config<NftBurnNodeConfig>(input, out var burn) || burn.NftId == Guid.Empty || burn.WalletId == Guid.Empty
            => "NFT burn requires an asset and signing wallet.",
        ActionNodeType.CredentialRevoke when !Config<CredentialRevokeNodeConfig>(input, out var revoke)
            || string.IsNullOrWhiteSpace(revoke.AssetId) || string.IsNullOrWhiteSpace(revoke.ChainType)
            => "Credential revocation requires chain and asset identity.",
        _ => null,
    };

    private static string Operation(ActionNodeType type) => type switch
    {
        ActionNodeType.NftMint or ActionNodeType.Grant => "Mint", ActionNodeType.NftTransfer or ActionNodeType.Transfer => "Transfer",
        ActionNodeType.NftBurn => "Burn", ActionNodeType.Refund => "Refund",
        ActionNodeType.CredentialRevoke => "Revoke", _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static bool MintConfig(ActionFrozenNodeInput input, out NftMintRequest mint)
    {
        if (input.NodeType == ActionNodeType.NftMint) return Config(input, out mint);
        var valid = Config<GrantNodeConfig>(input, out var grant);
        mint = grant?.Request!;
        return valid && mint is not null;
    }

    private static bool NeedsWorkflowCostConsent(ActionFrozenNodeInput input)
        => (input.NodeType is ActionNodeType.NftMint or ActionNodeType.Grant)
            && MintConfig(input, out var mint) && mint.ChainId.Equals("Solana", StringComparison.OrdinalIgnoreCase);

    private static ActionExternalOperationObservation CostConsentPending(ActionExternalOperationReference reference)
        => new(ActionExternalOperationStatus.Pending, reference,
            Detail: "Review and explicitly accept creation costs for this workflow execution before NFT admission.", ChainType: "Solana");

    private static string? RefundSource(ActionFrozenNodeInput input, RefundNodeConfig refund)
    {
        var matches = new List<string>();
        foreach (var (nodeId, execution) in input.AllExecutions)
        {
            if (refund.TransferNodeId is { } selected && selected != nodeId) continue;
            if (execution.RunId != input.RunId || execution.NodeId != nodeId || execution.State != ActionNodeState.Succeeded
                || execution.ExternalOperation is not { Domain: "nft", OperationId: not null } reference || execution.FrozenInput is null) continue;
            var source = Parse(reference, execution.FrozenInput);
            if (source is null || source.RunId != input.RunId || source.NodeId != nodeId || source.ActorUserId != input.ActorUserId
                || source.ActingTenantId != input.ActingTenantId || source.NodeType is not (ActionNodeType.Transfer or ActionNodeType.NftTransfer)
                || !Config<NftTransferNodeConfig>(source, out var transfer) || transfer.NftId != refund.NftId) continue;
            matches.Add(reference.OperationId);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static ActionExternalOperationObservation Ambiguous(ActionExternalOperationReference reference, string? detail)
        => new(ActionExternalOperationStatus.Ambiguous, reference, Detail: detail);
}
