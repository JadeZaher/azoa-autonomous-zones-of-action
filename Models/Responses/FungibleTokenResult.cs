// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Outcome of a fungible-token (ASA) launch. Mirrors <see cref="AllocationResult"/>:
/// carries the AZOA-side references the tenant needs to reconcile (the custodial
/// wallet that was used/created and the created on-chain asset id), plus a
/// <see cref="Replayed"/> flag so a redelivered request can see that the original
/// launch — not a second one — is being returned.
/// </summary>
public sealed class FungibleTokenResult
{
    /// <summary>The avatar the token was launched for (the authorised principal).</summary>
    public Guid AvatarId { get; set; }

    /// <summary>The custodial wallet the token was launched from.</summary>
    public Guid WalletId { get; set; }

    /// <summary>The on-chain address of the custodial wallet (ASA admin/holder).</summary>
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary><c>true</c> when this call provisioned a brand-new wallet.</summary>
    public bool WalletProvisioned { get; set; }

    /// <summary>The chain-native asset id of the created fungible token (ASA id).</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>The idempotency key the launch was deduped on (diagnostics).</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> when this response replays a prior launch under the same
    /// idempotency key — no second token was created.
    /// </summary>
    public bool Replayed { get; set; }
}
