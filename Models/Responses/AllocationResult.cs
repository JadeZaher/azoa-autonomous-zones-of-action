// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Outcome the fiat-settlement tenant records against its own investment row.
/// Carries the AZOA-side references the tenant needs to reconcile (the
/// custodial wallet that was used/created and the blockchain operation that
/// moved the asset), plus a <see cref="Replayed"/> flag so a redelivered
/// webhook can see that the original allocation — not a second one — is being
/// returned.
/// </summary>
public sealed class AllocationResult
{
    /// <summary>The avatar the allocation targeted (the authorised principal).</summary>
    public Guid AvatarId { get; set; }

    /// <summary>The custodial wallet the asset was allocated into.</summary>
    public Guid WalletId { get; set; }

    /// <summary>The on-chain address of the custodial wallet.</summary>
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary><c>true</c> when this call provisioned a brand-new wallet.</summary>
    public bool WalletProvisioned { get; set; }

    /// <summary>The blockchain operation id that performed the mint/transfer.</summary>
    public Guid OperationId { get; set; }

    /// <summary>
    /// <c>true</c> when this response replays a prior allocation under the same
    /// idempotency key — no second mint/transfer was performed.
    /// </summary>
    public bool Replayed { get; set; }

    /// <summary>The caller-authoritative amount before the node fee was applied.</summary>
    public string GrossAmount { get; set; } = string.Empty;

    /// <summary>The node fee retained from this allocation in base units.</summary>
    public string NodeFeeAmount { get; set; } = string.Empty;

    /// <summary>The amount sent to the chain operation after the node fee.</summary>
    public string NetAmount { get; set; } = string.Empty;

    /// <summary>The immutable fee schedule version used for this allocation.</summary>
    public long NodeFeeScheduleVersion { get; set; }
}
