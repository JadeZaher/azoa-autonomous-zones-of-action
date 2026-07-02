using System.Text.Json;
using AZOA.WebAPI.Models.Quest;

namespace AZOA.WebAPI.Services.Quest;

/// <summary>
/// Maps every <see cref="QuestNodeType"/> to its config DTO type.
/// Node types that take no config are registered explicitly as config-free
/// (null entry) — nothing escapes by accident.
/// See Services/Quest/AGENTS.md §node-config.
/// </summary>
public static class QuestNodeConfigRegistry
{
    // Type = null means "config-free" (any JSON is accepted; nothing to validate).
    private static readonly IReadOnlyDictionary<QuestNodeType, Type?> _map =
        new Dictionary<QuestNodeType, Type?>
        {
            // Holon ops — simple Id config or free-form model
            [QuestNodeType.HolonCreate]          = null,   // config is HolonCreateModel (open)
            [QuestNodeType.HolonUpdate]          = typeof(HolonUpdateNodeConfig),
            [QuestNodeType.HolonDelete]          = typeof(IdConfig),
            [QuestNodeType.HolonGet]             = typeof(IdConfig),
            [QuestNodeType.HolonQuery]           = null,   // free-form HolonQueryRequest
            [QuestNodeType.HolonInteract]        = typeof(HolonInteractNodeConfig),
            [QuestNodeType.HolonGetChildren]     = typeof(IdConfig),
            [QuestNodeType.HolonGetPeers]        = typeof(IdConfig),
            [QuestNodeType.HolonGetAncestors]    = typeof(IdConfig),
            [QuestNodeType.HolonGetDescendants]  = typeof(IdConfig),
            [QuestNodeType.HolonPropagate]       = typeof(HolonPropagateNodeConfig),
            [QuestNodeType.HolonCompose]         = null,   // free-form compose request
            [QuestNodeType.HolonClone]           = typeof(HolonCloneNodeConfig),
            [QuestNodeType.HolonMoveSubtree]     = typeof(HolonMoveNodeConfig),

            // NFT ops
            [QuestNodeType.NftMint]              = null,   // NftMintRequest (open)
            [QuestNodeType.NftTransfer]          = typeof(NftTransferNodeConfig),
            [QuestNodeType.NftBurn]              = typeof(NftBurnNodeConfig),
            [QuestNodeType.NftGet]               = typeof(IdConfig),
            [QuestNodeType.NftQuery]             = null,
            [QuestNodeType.NftGetMetadata]       = typeof(IdConfig),

            // Wallet ops
            [QuestNodeType.WalletCreate]         = null,
            [QuestNodeType.WalletUpdate]         = typeof(WalletUpdateNodeConfig),
            [QuestNodeType.WalletDelete]         = typeof(IdConfig),
            [QuestNodeType.WalletGet]            = typeof(IdConfig),
            [QuestNodeType.WalletQuery]          = null,
            [QuestNodeType.WalletSetDefault]     = typeof(WalletSetDefaultNodeConfig),
            [QuestNodeType.WalletGetPortfolio]   = typeof(IdConfig),

            // STAR ops
            [QuestNodeType.StarGenerate]         = typeof(StarGenerateNodeConfig),
            [QuestNodeType.StarDeploy]           = typeof(IdConfig),

            // Search / Avatar NFT / Blockchain
            [QuestNodeType.Search]               = null,
            [QuestNodeType.AvatarNFTGetComposite]= typeof(IdConfig),
            [QuestNodeType.BlockchainExecute]    = null,

            // Control-flow
            [QuestNodeType.Condition]            = null,   // pass-through; no config required
            [QuestNodeType.ComposeOutputs]       = null,

            // Tier-1 economic nodes (chain-free)
            [QuestNodeType.GateCheck]            = typeof(GateCheckNodeConfig),
            [QuestNodeType.Emit]                 = typeof(EmitNodeConfig),

            // Tier-2 economic nodes (RequiresChainCapability)
            [QuestNodeType.Swap]                 = typeof(SwapNodeConfig),
            [QuestNodeType.Grant]                = typeof(GrantNodeConfig),
            [QuestNodeType.Transfer]             = typeof(TransferNodeConfig),
            [QuestNodeType.Refund]               = typeof(RefundNodeConfig),
            [QuestNodeType.FungibleTokenCreate]  = typeof(FungibleTokenCreateNodeConfig),
        };

    /// <summary>
    /// Returns the config DTO type for <paramref name="nodeType"/>, or null for
    /// config-free node types. Throws <see cref="NotSupportedException"/> if the
    /// node type has no registry entry (catches newly-added types that weren't
    /// wired in).
    /// </summary>
    public static Type? GetConfigType(QuestNodeType nodeType)
    {
        if (!_map.TryGetValue(nodeType, out var configType))
            throw new NotSupportedException($"QuestNodeType.{nodeType} has no config registry entry. Add it to QuestNodeConfigRegistry.");
        return configType;
    }

    /// <summary>
    /// Validates config JSON for <paramref name="nodeType"/> using strict
    /// deserialization. Returns null on success, or an error string.
    /// Config-free node types always return null.
    /// </summary>
    public static string? Validate(QuestNodeType nodeType, string? configJson)
    {
        var configType = GetConfigType(nodeType);
        if (configType == null) return null;  // config-free

        try
        {
            var result = JsonSerializer.Deserialize(
                configJson ?? "{}",
                configType,
                QuestNodeConfig.StrictOptions);

            return result == null
                ? $"[{nodeType}] config deserialized to null."
                : null;
        }
        catch (JsonException ex)
        {
            return $"[{nodeType}] config parse error: {ex.Message}";
        }
    }
}
