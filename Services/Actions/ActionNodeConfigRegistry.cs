using System.Text.Json;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions.Predicates;

namespace AZOA.WebAPI.Services.Actions;

/// <summary>
/// Maps every <see cref="ActionNodeType"/> to its config DTO type.
/// Node types that take no config are registered explicitly as config-free
/// (null entry) — nothing escapes by accident.
/// See Services/Actions/AGENTS.md §node-config.
/// </summary>
public static class ActionNodeConfigRegistry
{
    // Type = null means "config-free" (any JSON is accepted; nothing to validate).
    private static readonly IReadOnlyDictionary<ActionNodeType, Type?> _map =
        new Dictionary<ActionNodeType, Type?>
        {
            // Holon ops — simple Id config or free-form model
            [ActionNodeType.HolonCreate]          = null,   // config is HolonCreateModel (open)
            [ActionNodeType.HolonUpdate]          = typeof(HolonUpdateNodeConfig),
            [ActionNodeType.HolonDelete]          = typeof(IdConfig),
            [ActionNodeType.HolonGet]             = typeof(IdConfig),
            [ActionNodeType.HolonQuery]           = null,   // free-form HolonQueryRequest
            [ActionNodeType.HolonInteract]        = typeof(HolonInteractNodeConfig),
            [ActionNodeType.HolonGetChildren]     = typeof(IdConfig),
            [ActionNodeType.HolonGetPeers]        = typeof(IdConfig),
            [ActionNodeType.HolonGetAncestors]    = typeof(IdConfig),
            [ActionNodeType.HolonGetDescendants]  = typeof(IdConfig),
            [ActionNodeType.HolonPropagate]       = typeof(HolonPropagateNodeConfig),
            [ActionNodeType.HolonCompose]         = null,   // free-form compose request
            [ActionNodeType.HolonClone]           = typeof(HolonCloneNodeConfig),
            [ActionNodeType.HolonMoveSubtree]     = typeof(HolonMoveNodeConfig),

            // NFT ops
            [ActionNodeType.NftMint]              = null,   // NftMintRequest (open)
            [ActionNodeType.NftTransfer]          = typeof(NftTransferNodeConfig),
            [ActionNodeType.NftBurn]              = typeof(NftBurnNodeConfig),
            [ActionNodeType.NftGet]               = typeof(IdConfig),
            [ActionNodeType.NftQuery]             = null,
            [ActionNodeType.NftGetMetadata]       = typeof(IdConfig),

            // Wallet ops
            [ActionNodeType.WalletCreate]         = null,
            [ActionNodeType.WalletUpdate]         = typeof(WalletUpdateNodeConfig),
            [ActionNodeType.WalletDelete]         = typeof(IdConfig),
            [ActionNodeType.WalletGet]            = typeof(IdConfig),
            [ActionNodeType.WalletQuery]          = null,
            [ActionNodeType.WalletSetDefault]     = typeof(WalletSetDefaultNodeConfig),
            [ActionNodeType.WalletGetPortfolio]   = typeof(IdConfig),

            // Action graph bundle ops
            [ActionNodeType.ActionGraphBundleGenerate]         = typeof(ActionGraphBundleGenerateNodeConfig),
            [ActionNodeType.ActionGraphBundleDeploy]           = typeof(IdConfig),

            // Search / User NFT / Blockchain
            [ActionNodeType.Search]               = null,
            [ActionNodeType.UserNFTGetComposite]= typeof(IdConfig),
            [ActionNodeType.BlockchainExecute]    = null,

            // Control-flow
            [ActionNodeType.Condition]            = null,   // pass-through; no config required
            [ActionNodeType.ComposeOutputs]       = null,

            // Tier-1 economic nodes (chain-free)
            [ActionNodeType.GateCheck]            = typeof(GateCheckNodeConfig),
            [ActionNodeType.Emit]                 = typeof(EmitNodeConfig),

            // Tier-2 economic nodes (RequiresChainCapability)
            [ActionNodeType.Swap]                 = typeof(SwapNodeConfig),
            [ActionNodeType.Grant]                = typeof(GrantNodeConfig),
            [ActionNodeType.Transfer]             = typeof(TransferNodeConfig),
            [ActionNodeType.Refund]               = typeof(RefundNodeConfig),
            [ActionNodeType.FungibleTokenCreate]  = typeof(FungibleTokenCreateNodeConfig),

            // Fractionalization rails (final-hardening D1) — route through the real bridge.
            [ActionNodeType.Bridge]               = typeof(BridgeNodeConfig),
            [ActionNodeType.Back]                 = typeof(BackNodeConfig),

            // Phase 5 D19 verifiable-credential rail (Tier-2, reaches an ASA clawback).
            [ActionNodeType.CredentialRevoke]     = typeof(CredentialRevokeNodeConfig),

            // Phase 5 D20 governance rails (Tier-1, pure holon state).
            // These three config records are declared beside their handlers, so they
            // live in the Handlers namespace rather than Models/Actions/NodeConfigs.cs.
            [ActionNodeType.GovernanceProposalCreate] = typeof(Handlers.GovernanceProposalCreateNodeConfig),
            [ActionNodeType.GovernanceVote]           = typeof(Handlers.GovernanceVoteNodeConfig),
            [ActionNodeType.GovernanceTally]          = typeof(Handlers.GovernanceTallyNodeConfig),

            // Phase 5 D21 credit rails (Tier-1, pure holon state).
            [ActionNodeType.LoanOpen]             = typeof(LoanOpenNodeConfig),
            [ActionNodeType.LoanRepaymentRecord]  = typeof(LoanRepaymentRecordNodeConfig),
            [ActionNodeType.LoanBalance]          = typeof(LoanBalanceNodeConfig),

            // Hold rails (D14 / W3.4) -- configs are declared beside their handlers.
            [ActionNodeType.Hold]                 = typeof(Handlers.HoldNodeConfig),
            [ActionNodeType.Release]              = typeof(Handlers.ReleaseNodeConfig),
            [ActionNodeType.HoldRefund]           = typeof(Handlers.HoldRefundNodeConfig),
            [ActionNodeType.ReleaseIntoHold]      = typeof(Handlers.ReleaseIntoHoldNodeConfig),
        };

    /// <summary>
    /// Returns the config DTO type for <paramref name="nodeType"/>, or null for
    /// config-free node types. Throws <see cref="NotSupportedException"/> if the
    /// node type has no registry entry (catches newly-added types that weren't
    /// wired in).
    /// </summary>
    public static Type? GetConfigType(ActionNodeType nodeType)
    {
        if (!_map.TryGetValue(nodeType, out var configType))
            throw new NotSupportedException($"ActionNodeType.{nodeType} has no config registry entry. Add it to ActionNodeConfigRegistry.");
        return configType;
    }

    /// <summary>
    /// Validates config JSON for <paramref name="nodeType"/> using strict
    /// deserialization. Returns null on success, or an error string.
    /// Config-free node types always return null.
    /// Binding pre-pass (AC-1d, AC-1e): validates $from syntax and (when
    /// <paramref name="directUpstreamNames"/> is provided) graph ancestry.
    /// </summary>
    /// <param name="nodeType">The node type to validate for.</param>
    /// <param name="configJson">Raw JSON config (may be null).</param>
    /// <param name="directUpstreamNames">
    /// When non-null (publish-time), every upstream.&lt;name&gt; prefix must
    /// be in this set. Pass null at definition-time (edges may not exist yet).
    /// </param>
    /// <param name="allNodeNames">
    /// When non-null (publish-time), every run.&lt;name&gt; prefix must name a
    /// node that exists ANYWHERE in the action (run.-root is run-scoped, not
    /// edge-scoped). Pass null at definition-time (grammar-only check still runs).
    /// </param>
    public static string? Validate(
        ActionNodeType nodeType,
        string? configJson,
        IReadOnlySet<string>? directUpstreamNames = null,
        IReadOnlySet<string>? allNodeNames = null)
    {
        // Step 1: binding structural check + path grammar.
        var bindingErr = ValidateBindings(configJson, directUpstreamNames, allNodeNames);
        if (bindingErr is not null) return bindingErr;

        // Step 2: strict round-trip on the $from-stripped shadow (V1).
        // Strip $from property values so a bound field (which is now absent)
        // passes the unknown-member check — the handler will receive the
        // resolved value at runtime, not the binding object.
        var shadowJson = StripBindings(configJson);

        var configType = GetConfigType(nodeType);
        if (configType == null) return null;  // config-free

        try
        {
            var result = JsonSerializer.Deserialize(
                shadowJson ?? "{}",
                configType,
                ActionNodeConfig.StrictOptions);

            return result == null
                ? $"[{nodeType}] config deserialized to null."
                : null;
        }
        catch (JsonException ex)
        {
            return $"[{nodeType}] config parse error: {ex.Message}";
        }
    }

    /// <summary>
    /// Validates $from binding syntax in <paramref name="configJson"/>.
    /// Returns null on success, or an error string on first violation.
    /// </summary>
    private static string? ValidateBindings(
        string? configJson,
        IReadOnlySet<string>? directUpstreamNames,
        IReadOnlySet<string>? allNodeNames = null)
    {
        var structErr = ActionConfigBindingResolver.FindAndValidateBindings(configJson, out var paths);
        if (structErr is not null) return structErr;

        foreach (var path in paths)
        {
            if (!GatePath.TryParse(path, out var segments, out var parseError))
                return $"$from '{path}': {parseError}";

            var root = segments[0];

            if (root == "upstream")
            {
                if (segments.Count < 3)
                    return $"$from '{path}': upstream path requires at least 3 segments.";

                // At publish time, validate the upstream node name exists as a direct predecessor.
                if (directUpstreamNames is not null && !directUpstreamNames.Contains(segments[1]))
                    return $"$from '{path}': upstream node '{segments[1]}' is not a direct upstream " +
                           "of this node (must be a source of an incoming edge).";
            }
            else if (root == "run")
            {
                if (segments.Count < 3)
                    return $"$from '{path}': run path requires at least 3 segments.";

                // run.-root is intentionally broader than upstream.-root: any prior
                // node in the run, not just a direct predecessor. We can only check
                // that the named node EXISTS in the action (ancestry/type checking is
                // the separate executability validator's job).
                if (allNodeNames is not null && !allNodeNames.Contains(segments[1]))
                    return $"$from '{path}': run node '{segments[1]}' does not exist in this action.";
            }
            else if (root == "holon")
            {
                if (segments.Count < 3)
                    return $"$from '{path}': holon path requires at least 3 segments.";

                // Guid-syntax check on the second segment (holon id).
                if (!GatePath.IsGuidShaped(segments[1]))
                    return $"$from '{path}': holon id '{segments[1]}' is not a valid GUID.";
            }
        }

        return null;
    }

    /// <summary>
    /// Returns a shadow copy of <paramref name="configJson"/> with all
    /// <c>{"$from":"..."}</c> property values removed (the property is dropped,
    /// not replaced), so the remaining JSON can be round-trip validated without
    /// the binding objects tripping the unknown-member check. (V1: strip
    /// property-value bindings only — array-element bindings are already errors.)
    /// </summary>
    private static string? StripBindings(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return configJson;
        if (!configJson.Contains("\"$from\"", StringComparison.Ordinal)) return configJson;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var stripped = StripBindingElement(doc.RootElement);
            return JsonSerializer.Serialize(stripped);
        }
        catch (JsonException)
        {
            return configJson; // malformed — let the deserialization below surface the error
        }
    }

    private static object? StripBindingElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // If this object is itself a $from binding with exactly one key, drop it
            // (return null so the parent omits this property).
            if (element.EnumerateObject().Count() == 1
                && element.TryGetProperty("$from", out _))
                return null;  // sentinel: caller drops this property

            var dict = new Dictionary<string, object?>();
            foreach (var prop in element.EnumerateObject())
            {
                var stripped = StripBindingElement(prop.Value);
                // null sentinel from above → omit the property from the shadow
                if (stripped is not null || prop.Value.ValueKind == JsonValueKind.Null)
                    dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : stripped;
            }
            return dict;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var list = new List<object?>();
            foreach (var item in element.EnumerateArray())
                list.Add(StripBindingElement(item));
            return list;
        }
        else
        {
            // Scalar — return as-is via the element's raw representation.
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetDouble(out var d) ? (object)d : element.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
    }
}
