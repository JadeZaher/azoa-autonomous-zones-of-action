using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Binds a workflow cost-consent request to one actor, execution and immutable NFT reference.</summary>
public static class NftWorkflowCostAcceptanceIdentity
{
    public static string Id(Guid actor, Guid? tenant, Guid executionId, Guid requestId)
        => CanonicalJson.Hash(new { domain = "nft-workflow-cost-acceptance/1", actor, tenant, executionId, requestId });

    public static string RequestHash(NftWorkflowCostAcceptance request, string lifecycleId, string frozenInputHash)
        => CanonicalJson.Hash(new { domain = "nft-workflow-cost-request/1", request, lifecycleId, frozenInputHash });

    public static bool Matches(NftWorkflowCostAcceptanceReceipt receipt)
    {
        var request = receipt.Request;
        var reference = receipt.Reference;
        var original = receipt.OriginalAuthority;
        var accepting = receipt.AcceptingAuthority;
        if (receipt.Version != 1 || request is null || reference is null || original is null || accepting is null
            || original.Action is null || original.Credential is null || accepting.Credential is null
            || original.RequiredScopes is null || accepting.RequiredScopes is null
            || original.Credential.Version != 1 || accepting.Credential.Version != 1
            || request.RequestId == Guid.Empty || request.ExecutionId == Guid.Empty || request.ExpectedExecutionVersion < 0
            || request.ExpectedExecutionVersion == long.MaxValue || reference.Version != 1 || reference.Domain != "nft"
            || reference.ActorUserId == Guid.Empty || original.Version != 1 || accepting.Version != 1
            || reference.IntentHash is not { Length: 64 } || !reference.IntentHash.All(char.IsAsciiHexDigitLower)
            || !NftProtocolCostContract.IsAuthorized(receipt.Quote, request.Authorization)) return false;
        var action = original.Action;
        var actor = reference.ActorUserId;
        var tenant = reference.ActingTenantId;
        var key = $"action:{action.RunId:N}:{action.NodeId:N}:nft";
        return action.RunId != Guid.Empty && action.NodeId != Guid.Empty && reference.RequestKey == key
            && reference.OperationId == NftLifecycleIdentity.ForRequest(actor, "Mint", key, tenant ?? Guid.Empty)
            && action.ActorUserId == actor && action.ActingTenantId == tenant && action.IntentHash == reference.IntentHash
            && original.Credential.ActorUserId == actor && original.Credential.TenantId == tenant
            && accepting.Credential.ActorUserId == actor && accepting.Credential.TenantId == tenant
            && original.RequiredScopes.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { AzoaScopes.ActionExecute, AzoaScopes.NftMint }.Order(StringComparer.Ordinal))
            && accepting.RequiredScopes.SequenceEqual(new[] { AzoaScopes.NftMint })
            && receipt.Quote.LifecycleId == reference.OperationId && receipt.Quote.ActorUserId == actor
            && receipt.Quote.ActingTenantId == (tenant ?? Guid.Empty) && receipt.Quote.Stage == "Create"
            && receipt.Quote.PreparationGeneration == 0 && receipt.Quote.QuotedIntentHash is { Length: 64 };
    }
}
