using System.Text.Json;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Services.Actions;

namespace AZOA.WebAPI.Helpers;

/// <summary>Reads the exact admitted mint draft without altering frozen workflow input.</summary>
public static class NftWorkflowMintDraft
{
    public static UserNFTMintModel? Read(string frozenInput, ActionExternalOperationReference reference)
    {
        if (CanonicalJson.HashCanonical(frozenInput) != reference.IntentHash) return null;
        var input = JsonSerializer.Deserialize<ActionFrozenNodeInput>(frozenInput);
        if (input is null || input.Version != 1 || input.ActorUserId != reference.ActorUserId
            || input.ActingTenantId != reference.ActingTenantId
            || reference.RequestKey != $"action:{input.RunId:N}:{input.NodeId:N}:nft") return null;
        NftMintRequest? mint = null;
        if (input.NodeType == ActionNodeType.NftMint && ActionNodeConfig.TryDeserialize<NftMintRequest>(
            input.Config.GetRawText(), input.NodeType.ToString(), out var direct, out _)) mint = direct;
        else if (input.NodeType == ActionNodeType.Grant && ActionNodeConfig.TryDeserialize<GrantNodeConfig>(
            input.Config.GetRawText(), input.NodeType.ToString(), out var grant, out _)) mint = grant.Request;
        return mint is null || mint.WalletId == Guid.Empty || string.IsNullOrWhiteSpace(mint.MetadataUri)
            || string.IsNullOrWhiteSpace(mint.ChainId) || !string.IsNullOrWhiteSpace(mint.TokenId)
            ? null : NftLifecycleOperationProjection.MintModel(mint);
    }
}
