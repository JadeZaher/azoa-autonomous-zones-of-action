using System.Text.Json;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Quest;

// ═══════════════════════════════════════════════════════════════════
// Per-node-type config DTOs for quest node dispatch deserialization.
// Moved verbatim from the former QuestManager.cs:~845-908 (made public so
// the scoped handlers in Services/Quest/Handlers/* can deserialize them).
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

public class StarGenerateNodeConfig
{
    public Guid StarId { get; set; }
    public STARDappGenerationRequest Request { get; set; } = new();
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

    /// <summary>
    /// Optional tenant-defined event name for the GENERIC quest.emit webhook
    /// (final-hardening F3). When the run has an acting tenant AND that tenant has a
    /// webhook registration, the Emit node ALSO enqueues an outbox event with this name
    /// (defaults to <c>quest.emit</c> when omitted). Free-form — AZOA does not interpret
    /// it; it is echoed to the receiver so the tenant can route on it. The webhook is a
    /// best-effort push ON TOP of the node's serialized output, which remains the
    /// authoritative settlement surface.
    /// </summary>
    public string? EventType { get; set; }
}

/// <summary>Swap config: tenant-supplied DEX swap params. Rate comes from the DEX, never AZOA.</summary>
public class SwapNodeConfig
{
    public SwapExecuteRequest Request { get; set; } = new();
}

/// <summary>Grant (mint-to-actor) config. Actor avatar is taken from the run context, never this body.</summary>
public class GrantNodeConfig
{
    public NftMintRequest Request { get; set; } = new();
    /// <summary>Optional holon to link to the minted asset (Holon.token_id/chain_id + OperationLog link).</summary>
    public Guid? HolonId { get; set; }
}

/// <summary>Transfer (move-to-actor) config. Actor avatar from run context.</summary>
public class TransferNodeConfig
{
    public Guid NftId { get; set; }
    public NftTransferRequest Request { get; set; } = new();
}

/// <summary>Refund (reverse transfer / clawback-deferred) config. Actor from run context.</summary>
public class RefundNodeConfig
{
    public Guid NftId { get; set; }
    public NftTransferRequest Request { get; set; } = new();
}

/// <summary>FungibleTokenCreate config: launch a fungible token (ASA) optionally
/// linked to a holon. Total supply + decimals are tenant-supplied and authoritative;
/// AZOA derives no economic meaning (peg/valuation is tenant-side).</summary>
public class FungibleTokenCreateNodeConfig
{
    public string ChainType { get; set; } = "Algorand";
    public string Name { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    public ulong Total { get; set; }
    public int Decimals { get; set; }
    public Guid? HolonId { get; set; }
}

/// <summary>
/// Bridge node config (final-hardening D1): lock/bridge an asset from
/// <see cref="SourceChain"/> to <see cref="TargetChain"/> via the real Phase-B
/// <c>ICrossChainBridgeService.InitiateBridgeAsync</c>. The node MOVES value only —
/// it derives no economic meaning; peg/valuation stays tenant-side (Emit). The
/// actor avatar is taken from the run context (never a config-body avatar). On an
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

    /// <summary>Units to bridge. Must be positive; the bridge rejects &lt;= 0.</summary>
    [JsonConverter(typeof(UlongDecimalStringJsonConverter))]
    public ulong Amount { get; set; } = 1;

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
/// run-context avatar; the reverse is IDOR-scoped to that avatar's own bridge rows.
/// </summary>
public class BackNodeConfig
{
    /// <summary>Id of the forward bridge transaction to reverse (from the upstream Bridge node output).</summary>
    public string BridgeTransactionId { get; set; } = string.Empty;

    /// <summary>Source-chain address to release the original asset back to.</summary>
    public string SourceRecipientAddress { get; set; } = string.Empty;
}
