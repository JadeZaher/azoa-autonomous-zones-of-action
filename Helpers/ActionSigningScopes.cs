using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Helpers;

/// <summary>Declares signing scope requirements from resolved immutable action input.</summary>
public static class ActionSigningScopes
{
    public static IReadOnlyList<string>? Required(ActionNodeType type, string configuration) => type switch
    {
        ActionNodeType.NftMint or ActionNodeType.Grant => [AzoaScopes.NftMint],
        ActionNodeType.NftTransfer or ActionNodeType.Transfer or ActionNodeType.Refund => [AzoaScopes.TransferSign],
        ActionNodeType.Hold or ActionNodeType.Release or ActionNodeType.HoldRefund or ActionNodeType.ReleaseIntoHold => [AzoaScopes.TransferSign],
        ActionNodeType.NftBurn => [AzoaScopes.NftBurn],
        ActionNodeType.CredentialRevoke => [AzoaScopes.CredentialRevokeSign],
        ActionNodeType.FungibleTokenCreate => [AzoaScopes.TokenCreateSign],
        ActionNodeType.Swap => [AzoaScopes.SwapSign],
        ActionNodeType.Bridge or ActionNodeType.Back => [AzoaScopes.WttSourceSign],
        ActionNodeType.ActionGraphBundleDeploy => Deployment(configuration),
        ActionNodeType.HolonCreate or ActionNodeType.HolonUpdate or ActionNodeType.HolonDelete or ActionNodeType.HolonGet
            or ActionNodeType.HolonQuery or ActionNodeType.HolonInteract or ActionNodeType.HolonGetChildren or ActionNodeType.HolonGetPeers
            or ActionNodeType.HolonGetAncestors or ActionNodeType.HolonGetDescendants or ActionNodeType.HolonPropagate
            or ActionNodeType.HolonCompose or ActionNodeType.HolonClone or ActionNodeType.HolonMoveSubtree
            or ActionNodeType.NftGet or ActionNodeType.NftQuery or ActionNodeType.NftGetMetadata
            or ActionNodeType.WalletCreate or ActionNodeType.WalletUpdate or ActionNodeType.WalletDelete or ActionNodeType.WalletGet
            or ActionNodeType.WalletQuery or ActionNodeType.WalletSetDefault or ActionNodeType.WalletGetPortfolio
            or ActionNodeType.ActionGraphBundleGenerate or ActionNodeType.Search or ActionNodeType.UserNFTGetComposite
            or ActionNodeType.BlockchainExecute or ActionNodeType.Condition or ActionNodeType.ComposeOutputs
            or ActionNodeType.GateCheck or ActionNodeType.Emit or ActionNodeType.GovernanceProposalCreate
            or ActionNodeType.GovernanceVote or ActionNodeType.GovernanceTally or ActionNodeType.LoanOpen
            or ActionNodeType.LoanRepaymentRecord or ActionNodeType.LoanBalance => [],
        _ => null,
    };

    private static IReadOnlyList<string>? Deployment(string configuration)
    {
        if (!ActionNodeConfig.TryDeserialize<DeploymentNodeConfig>(configuration, "Deployment", out var config, out _)
            || config.Deployment is not { Target: not null } request) return null;
        if (request.Target.Kind == "PlatformRuntime") return request.NativeIntent is null ? [] : null;
        if (request.Target.Kind != "NativeContract" || request.NativeIntent is not { } intent) return null;
        return intent.Kind switch
        {
            "Deploy" or "Recovery" => [AzoaScopes.ContractDeploySign],
            "Call" => [AzoaScopes.ContractCallSign],
            "Account" when string.Equals(intent.ChainType, "Solana", StringComparison.OrdinalIgnoreCase) => [AzoaScopes.SolanaAccountSign],
            _ => null,
        };
    }
}
