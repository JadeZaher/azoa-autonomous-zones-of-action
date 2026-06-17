using System.Text.Json;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;

namespace OASIS.WebAPI.Models.Quest;

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
/// <c>upstream.&lt;nodeName&gt;.&lt;jsonPath&gt;</c>) and injected reads
/// (<c>reads.&lt;name&gt;</c>). <see cref="Reads"/> supplies tenant-injected
/// read values by name. No economics: OASIS only compares.
/// </summary>
public class GateCheckNodeConfig
{
    public string Predicate { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Reads { get; set; } = new();
}

/// <summary>
/// Emit config: an opaque tenant-shaped payload serialized to the node's
/// output. OASIS holds no settlement/fiat/payout state (tenant settles).
/// </summary>
public class EmitNodeConfig
{
    public JsonElement Payload { get; set; }
}

/// <summary>Swap config: tenant-supplied DEX swap params. Rate comes from the DEX, never OASIS.</summary>
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
