using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Services.Actions;

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
        ActionNodeType.Bridge or ActionNodeType.Back => Bridge(configuration),
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

    /// <summary>X-1: the WTT route needs both payer scopes; a legacy trusted-bridge node signs a transfer. Mirrors
    /// <c>ActionNodeHandlerRegistry.TryGetFor</c> so admission, the store fence and participant re-authorization agree.</summary>
    private static IReadOnlyList<string>? Bridge(string configuration)
    {
        try { return WttActionResolver.ReadConfig(configuration) is not null ? WttActionResolver.SigningScopes : [AzoaScopes.TransferSign]; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static IReadOnlyList<string>? Deployment(string configuration)
    {
        if (!ActionNodeConfig.TryDeserialize<DeploymentNodeConfig>(configuration, "Deployment", out var config, out _)
            || config.Deployment is not { Target: not null } request) return null;
        if (request.Target.Kind == "PlatformRuntime") return request.NativeIntent is null ? [] : null;
        if (request.Target.Kind != "NativeContract" || request.NativeIntent is not { } intent) return null;
        return ForNativeIntent(intent.Kind, intent.ChainType) is { } scope ? [scope] : null;
    }

    /// <summary>The single signing scope a native deployment intent kind requires; null when the kind is unsupported on the chain.</summary>
    public static string? ForNativeIntent(string? kind, string? chainType) => kind switch
    {
        "Deploy" or "Recovery" => AzoaScopes.ContractDeploySign,
        "Call" => AzoaScopes.ContractCallSign,
        "Account" when string.Equals(chainType, "Solana", StringComparison.OrdinalIgnoreCase) => AzoaScopes.SolanaAccountSign,
        _ => null,
    };
}
