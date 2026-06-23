using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// tenant-consent-delegation C1/AC4: the LIVE consent check the single custody
/// chokepoint (<c>KeyCustodyService</c>) calls BEFORE any key decrypt on a
/// tenant-driven signing action.
/// <para>
/// The gate is the enforcement path (AC8): a revoked/expired/absent grant denies the
/// sign on the very NEXT attempt, independent of webhook delivery and of any
/// background job (AC5/M4). It also writes the immutable per-sign audit row (AC10).
/// </para>
/// <para>
/// It is a NO-OP allow when the action is NOT tenant-driven
/// (<see cref="SigningContext.IsTenantDriven"/> is false) — a user signing their own
/// key, or a pure platform-internal op, needs no grant. Only an action that carries
/// an <c>ActingTenantId</c> is gated.
/// </para>
/// </summary>
public interface ITenantConsentGate
{
    /// <summary>
    /// Returns a non-error result iff the signing action described by
    /// <paramref name="ctx"/> is permitted. For a tenant-driven action this requires
    /// a live <c>ConsentGrant</c> (grantor=<see cref="SigningContext.GrantorAvatarId"/>,
    /// tenant=<see cref="SigningContext.ActingTenantId"/>, scope ⊇
    /// <see cref="SigningContext.Scope"/>, not revoked, not expired at <c>now</c>).
    /// FAILS CLOSED: any missing/ambiguous input on a tenant-driven action denies.
    /// Always writes an audit row for a tenant-driven decision (allowed or denied).
    /// </summary>
    Task<AZOAResult<bool>> EnsureAllowedAsync(SigningContext ctx, CancellationToken ct = default);
}
