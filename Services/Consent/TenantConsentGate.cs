// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Consent;

/// <summary>
/// The live consent check invoked by the single custody chokepoint
/// (<see cref="KeyCustodyService"/>) before any key decrypt (tenant-consent-delegation
/// C1/AC4/AC5/AC10). See <see cref="ITenantConsentGate"/>.
///
/// <para><b>Fail-closed posture.</b> Every guard returns a denial result rather than
/// throwing; an exception while looking up the grant is treated as a denial (a DB
/// hiccup must NOT open the gate). A tenant-driven sign with no covering live grant
/// is rejected even though <c>wallet.AvatarId == claimAvatarId</c> would otherwise
/// pass the legacy custody IDOR check — that is the entire point of C1.</para>
/// </summary>
public sealed class TenantConsentGate : ITenantConsentGate
{
    private readonly IConsentGrantStore _grants;
    private readonly IConsentAuditStore _audit;
    private readonly ILogger<TenantConsentGate> _logger;

    public TenantConsentGate(
        IConsentGrantStore grants,
        IConsentAuditStore audit,
        ILogger<TenantConsentGate> logger)
    {
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AZOAResult<bool>> EnsureAllowedAsync(SigningContext ctx, CancellationToken ct = default)
    {
        var result = new AZOAResult<bool> { Result = true };

        // Not tenant-driven ⇒ no grant required (user signs own key / platform-internal).
        if (!ctx.IsTenantDriven)
            return result;

        // ── Tenant-driven: fail closed unless a live covering grant exists. ──────
        var grantor = ctx.GrantorAvatarId;
        var tenant = ctx.ActingTenantId;
        var scope = ctx.Scope;
        var now = DateTime.UtcNow;

        if (grantor == Guid.Empty || string.IsNullOrWhiteSpace(scope))
        {
            // A tenant-driven action with no resolvable grantor or scope cannot be
            // consent-checked — deny. (e.g. a platform op that named a tenant but no
            // on-whose-behalf user, or an op with no declared signing scope.)
            await DenyAuditAsync(Guid.Empty, tenant, grantor, scope ?? string.Empty,
                "Tenant-driven sign missing grantor or scope.", now, ct);
            return Deny(result, "Tenant-driven signing is not permitted without a resolvable grantor and scope.");
        }

        ConsentGrant? grant;
        try
        {
            var lookup = await _grants.FindCoveringGrantAsync(grantor, tenant, scope, now, ct);
            // A store error is a denial — never open the gate on an infra failure.
            if (lookup.IsError)
            {
                _logger.LogWarning(
                    "Consent gate: grant lookup failed for grantor {Grantor} tenant {Tenant} scope {Scope}: {Msg}",
                    grantor, tenant, scope, lookup.Message);
                await DenyAuditAsync(Guid.Empty, tenant, grantor, scope,
                    "Grant lookup failed (fail-closed).", now, ct);
                return Deny(result, "Consent could not be verified; the signing action was denied.");
            }
            grant = lookup.Result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Consent gate: grant lookup threw for grantor {Grantor} tenant {Tenant} scope {Scope}.",
                grantor, tenant, scope);
            await DenyAuditAsync(Guid.Empty, tenant, grantor, scope,
                "Grant lookup threw (fail-closed).", now, ct);
            return Deny(result, "Consent could not be verified; the signing action was denied.");
        }

        // Defensive re-evaluation of liveness/coverage at the seam even though the
        // store query already filtered — the seam owns the final word (AC5/M4).
        if (grant is null || !grant.Covers(scope, now))
        {
            await DenyAuditAsync(grant?.Id ?? Guid.Empty, tenant, grantor, scope,
                grant is null ? "No covering grant." : "Grant not live / scope not covered.", now, ct);
            return Deny(result,
                "No live consent grant covers this tenant-driven signing action; the action was denied.");
        }

        // Allowed — record the immutable audit row (AC10) for the tenant-driven sign.
        await AuditAsync(new ConsentAuditEntry
        {
            Action = ConsentAuditAction.TenantSignAllowed,
            GrantId = grant.Id,
            TenantId = tenant,
            AvatarId = grantor,
            Scope = scope,
            OccurredAt = now,
        }, ct);

        result.Message = "Consent verified.";
        return result;
    }

    private async Task DenyAuditAsync(
        Guid grantId, Guid tenant, Guid grantor, string scope, string reason, DateTime now, CancellationToken ct)
        => await AuditAsync(new ConsentAuditEntry
        {
            Action = ConsentAuditAction.TenantSignDenied,
            GrantId = grantId,
            TenantId = tenant,
            AvatarId = grantor,
            Scope = scope,
            Detail = reason,
            OccurredAt = now,
        }, ct);

    private async Task AuditAsync(ConsentAuditEntry entry, CancellationToken ct)
    {
        // The audit write is best-effort durability: a failure is logged but must
        // not flip a denial into an allow (or vice-versa). The decision already
        // stands; the row is the record of it.
        try
        {
            var r = await _audit.AppendAsync(entry, ct);
            if (r.IsError)
                _logger.LogError("Consent audit append failed: {Msg}", r.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent audit append threw.");
        }
    }

    private static AZOAResult<bool> Deny(AZOAResult<bool> result, string message)
    {
        result.IsError = true;
        result.Result = false;
        result.Message = message;
        return result;
    }
}
