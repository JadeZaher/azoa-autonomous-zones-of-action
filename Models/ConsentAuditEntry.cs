// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models;

/// <summary>
/// The kind of consent-relevant event an audit row records (AC10/L1).
/// </summary>
public enum ConsentAuditAction
{
    Granted = 0,
    Revoked = 1,
    Expired = 2,

    /// <summary>A tenant-driven sign was ALLOWED by a live grant at the custody seam.</summary>
    TenantSignAllowed = 3,

    /// <summary>A tenant-driven sign was DENIED (no covering live grant) — fail-closed.</summary>
    TenantSignDenied = 4,
}

/// <summary>
/// An immutable, append-only audit row written for every grant, revoke, and
/// tenant-driven sign decision (tenant-consent-delegation AC10/L1). Independent of
/// the best-effort webhook: the audit is the durable record of what AZOA decided,
/// the webhook is only a notification. Rows are never updated or deleted.
/// </summary>
public sealed class ConsentAuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public ConsentAuditAction Action { get; set; }

    /// <summary>The grant this event concerns. <see cref="Guid.Empty"/> for a
    /// tenant-sign DENIED with no grant to point at.</summary>
    public Guid GrantId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The user (grantor) whose authority the event concerns.</summary>
    public Guid AvatarId { get; set; }

    /// <summary>The scope of the operation (for sign decisions) or a comma-joined
    /// scope list (for grant/revoke).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Optional free-form detail (e.g. the deny reason).</summary>
    public string? Detail { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
