using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Actions;

// ═══════════════════════════════════════════════════════════════════
// Per-node-type config DTOs for action node dispatch deserialization.
// Moved verbatim from the former ActionManager.cs:~845-908 (made public so
// the scoped handlers in Services/Actions/Handlers/* can deserialize them).
// ═══════════════════════════════════════════════════════════════════

public class IdConfig
{
    public Guid Id { get; set; }
}

public class HolonUpdateNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonUpdateModel Model { get; set; } = new();
}

public class HolonInteractNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonInteractionRequest Request { get; set; } = new();
}

public class HolonPropagateNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonPropagateRequest Request { get; set; } = new();
}

public class HolonCloneNodeConfig
{
    public Guid HolonId { get; set; }
    public HolonCloneRequest Request { get; set; } = new();
}

public class HolonMoveNodeConfig
{
    public Guid HolonId { get; set; }
    public Guid NewParentId { get; set; }
}

public class NftTransferNodeConfig
{
    public Guid NftId { get; set; }
    public NftTransferRequest Request { get; set; } = new();
}

public class NftBurnNodeConfig
{
    public Guid NftId { get; set; }
    public Guid WalletId { get; set; }
    public AZOA.WebAPI.Models.Blockchain.NftFeeAuthorization? FeeAuthorization { get; set; }
}

public class WalletUpdateNodeConfig
{
    public Guid WalletId { get; set; }
    public WalletUpdateModel Model { get; set; } = new();
}

public class WalletSetDefaultNodeConfig
{
    public Guid WalletId { get; set; }
}

public class ActionGraphBundleGenerateNodeConfig
{
    public Guid ActionGraphBundleId { get; set; }
    public ActionGraphBundleDappGenerationRequest Request { get; set; } = new();
}

/// <summary>
/// GateCheck predicate config. <see cref="Predicate"/> is a whitelisted
/// boolean expression over upstream outputs (referenced as
/// <c>upstream.&lt;nodeName&gt;.&lt;jsonPath&gt;</c>), injected reads
/// (<c>reads.&lt;name&gt;</c>), and holon lifecycle state
/// (<c>holon.&lt;id&gt;.&lt;field&gt;</c>). <see cref="Reads"/> supplies
/// tenant-injected read values by name; <see cref="Holons"/> lists the holon ids
/// whose CURRENT state the gate reads directly (smart-gates-holon-state §8.1). No
/// economics: AZOA only compares.
/// </summary>
public class GateCheckNodeConfig
{
    public string Predicate { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Reads { get; set; } = new();

    /// <summary>
    /// Holon ids whose CURRENT lifecycle state the predicate reads directly as
    /// <c>holon.&lt;id&gt;.&lt;field&gt;</c> (e.g.
    /// <c>holon.&lt;projectId&gt;.status == "FUNDED"</c>) — instead of threading the
    /// value through an upstream <c>HolonGet</c>. The handler resolves each id to a
    /// <c>holon.&lt;id&gt;</c> scope entry by reading the holon's live state; a
    /// missing/unreadable holon fails the gate closed (smart-gates-holon-state §8.1).
    /// </summary>
    public List<Guid> Holons { get; set; } = new();
}

/// <summary>
/// Emit config: an opaque tenant-shaped payload serialized to the node's
/// output. AZOA holds no settlement/fiat/payout state (tenant settles).
/// </summary>
public class EmitNodeConfig
{
    public JsonElement Payload { get; set; }

    /// <summary>Optional event name, defaulting to action.emit; see Services/Actions/AGENTS.md for durable acceptance.</summary>
    public string? EventType { get; set; }
}

/// <summary>Swap config: frozen exact-input quote request; price, receipt and costs come from the durable DEX journal.</summary>
public class SwapNodeConfig
{
    public DexQuoteRequest? Quote { get; set; }
}

/// <summary>Grant (mint-to-actor) config. Actor user is taken from the run context, never this body.</summary>
public class GrantNodeConfig
{
    public NftMintRequest Request { get; set; } = new();
    /// <summary>Optional holon to link to the minted asset (Holon.token_id/chain_id + OperationLog link).</summary>
    public Guid? HolonId { get; set; }
}

/// <summary>Transfer (move-to-actor) config. Actor user from run context.</summary>
public class TransferNodeConfig
{
    public Guid NftId { get; set; }
    public NftTransferRequest Request { get; set; } = new();
}

/// <summary>Refund (reverse transfer / clawback-deferred) config. Actor from run context.</summary>
public class RefundNodeConfig
{
    public Guid NftId { get; set; }
    public Guid? TransferNodeId { get; set; }
    public NftTransferRequest Request { get; set; } = new();
}

/// <summary>FungibleTokenCreate config: launch a fungible token (ASA) optionally
/// linked to a holon. Total supply + decimals are tenant-supplied and authoritative;
/// AZOA derives no economic meaning (peg/valuation is tenant-side).</summary>
public class FungibleTokenCreateNodeConfig
{
    public string ChainType { get; set; } = "Algorand";
    public AZOA.WebAPI.Core.ChainNetwork? Network { get; set; }
    public string Name { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.WriteAsString)]
    public ulong Total { get; set; }
    public int Decimals { get; set; }
    public Guid? HolonId { get; set; }
}

/// <summary>
/// Bridge node config (final-hardening D1): lock/bridge an asset from
/// <see cref="SourceChain"/> to <see cref="TargetChain"/> via the real Phase-B
/// <c>ICrossChainBridgeService.InitiateBridgeAsync</c>. The node MOVES value only —
/// it derives no economic meaning; peg/valuation stays tenant-side (Emit). The
/// actor user is taken from the run context (never a config-body user). On an
/// Real-chain routes remain fail-closed until a provider implements the complete
/// lock/mint/burn/release lifecycle; simulated routes remain available.
/// </summary>
public class BridgeNodeConfig
{
    /// <summary>Source chain to lock the asset on (e.g. "Algorand").</summary>
    public string SourceChain { get; set; } = "Algorand";

    /// <summary>Target chain to mint the wrapped/project asset on.</summary>
    public string TargetChain { get; set; } = string.Empty;

    /// <summary>The source-chain asset id (ASA id / token id) being bridged.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>Recipient address on the target chain.</summary>
    public string RecipientAddress { get; set; } = string.Empty;

    /// <summary>
    /// Units to bridge, in source-asset base units. Must be positive; the bridge rejects 0.
    /// D13: widened from <c>ulong</c>, wire form unchanged (a canonical decimal string).
    /// </summary>
    [JsonConverter(typeof(RailAmountBaseUnitStringJsonConverter))]
    public RailAmount Amount { get; set; } = RailAmount.OfChainAsset(1);

    /// <summary>
    /// Optional bridge mode ("Trusted" or "Wormhole"). Null lets the service pick
    /// its configured default. Parsed case-insensitively; an unrecognised value
    /// fails the node closed rather than silently defaulting.
    /// </summary>
    public string? Mode { get; set; }
}

/// <summary>
/// Back node config (final-hardening D1): the reverse of a prior <c>Bridge</c> —
/// burn the wrapped asset on the target chain and release the original on the
/// source chain via the real <c>ICrossChainBridgeService.ReverseBridgeAsync</c>.
/// <see cref="BridgeTransactionId"/> is the id returned by the upstream Bridge
/// node (typically supplied via a <c>$from</c> upstream binding). The actor is the
/// run-context user; the reverse is IDOR-scoped to that user's own bridge rows.
/// </summary>
public class BackNodeConfig
{
    /// <summary>Id of the forward bridge transaction to reverse (from the upstream Bridge node output).</summary>
    public string BridgeTransactionId { get; set; } = string.Empty;

    /// <summary>Source-chain address to release the original asset back to.</summary>
    public string SourceRecipientAddress { get; set; } = string.Empty;
}
