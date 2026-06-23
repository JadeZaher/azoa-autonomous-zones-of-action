// ─── DI registration (orchestrator applies to Program.cs) ───
//   builder.Services.AddScoped<IConsentManager, ConsentManager>();
//
//   IConsentGrantStore, IConsentAuditStore, and IConsentWebhookEmitter are
//   registered by the orchestrator alongside the other SurrealDB stores /
//   webhook infra (this manager only consumes them).

using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// The consent authority (tenant-consent-delegation §1). Owns the user-granted,
/// revocable <see cref="ConsentGrant"/> lifecycle: grant, revoke, list, and the
/// tenant offboard sweep. AZOA is the source of truth — this manager DECIDES
/// validity; the signing seam (owned elsewhere) ENFORCES it on every
/// tenant-driven sign, and the webhook (owned elsewhere) merely NOTIFIES.
///
/// Two security cruxes shape every method here:
///  • IDOR / isolation (AC9, L2, L3): the grantor is ALWAYS the authenticated
///    user (a parameter the controller fills from the token, never a body field).
///    A cross-user / cross-tenant probe returns
///    <see cref="TenantAuthorizationError.NotFound"/> (→ 404), never Forbidden —
///    indistinguishable from "no such grant" (mirrors <see cref="TenantManager"/>).
///  • H4 anti-forgery (AC6): a <see cref="GrantOrigin.Participation"/> grant —
///    the crypto-free join-driven standing grant — MUST exclude every
///    value-signing scope (<see cref="AzoaScopes.ValueSigningScopes"/>); a value
///    action requires a deliberate <c>UserExplicit</c>, user-authenticated grant.
///
/// Every grant and revoke writes an immutable audit row (AC10/L1) independent of
/// the best-effort webhook: a failed audit write is surfaced as an error result
/// but never silently masks the underlying decision (the grant/revoke already
/// persisted is the durable fact).
/// </summary>
public class ConsentManager : IConsentManager
{
    private readonly IConsentGrantStore _grants;
    private readonly IConsentAuditStore _audit;
    private readonly IConsentWebhookEmitter _webhook;

    public ConsentManager(IConsentGrantStore grants, IConsentAuditStore audit, IConsentWebhookEmitter webhook)
    {
        _grants = grants;
        _audit = audit;
        _webhook = webhook;
    }

    public async Task<AZOAResult<ConsentGrant>> GrantAsync(
        Guid grantorAvatarId,
        Guid tenantId,
        IEnumerable<string> scopes,
        GrantOrigin origin,
        string? participationRef,
        DateTime? expiresAt,
        CancellationToken ct = default)
    {
        var result = new AZOAResult<ConsentGrant>();

        // Normalize the requested scope set: trim, drop blanks, de-dup ordinally.
        // The grantor is the authenticated user passed by the controller — never a
        // body field (AC9); the body cannot even express a grantor.
        var normalizedScopes = NormalizeScopes(scopes);
        if (normalizedScopes.Count == 0)
        {
            result.IsError = true;
            result.Message = "At least one scope is required.";
            return result;
        }

        // An expiry in the past is meaningless — a grant that is dead on arrival.
        if (expiresAt is not null && expiresAt.Value <= DateTime.UtcNow)
        {
            result.IsError = true;
            result.Message = "expiresAt must be in the future.";
            return result;
        }

        // H4 (AC6): a Participation standing grant carries ONLY minimum non-value
        // scopes. Any value-signing scope under Participation origin is rejected —
        // value-signing requires a deliberate UserExplicit grant, never a side
        // effect of joining. UserExplicit grants may carry value scopes freely.
        if (origin == GrantOrigin.Participation)
        {
            var offending = normalizedScopes.Where(AzoaScopes.ValueSigningScopes.Contains).ToList();
            if (offending.Count > 0)
            {
                result.IsError = true;
                result.Message =
                    "A Participation grant cannot carry value-signing scopes (" +
                    string.Join(", ", offending) +
                    "); these require a UserExplicit grant (H4).";
                return result;
            }

            // A Participation grant is meaningless without the ref that offboarding
            // revokes by (L3); reject a participation grant with no reference.
            if (string.IsNullOrWhiteSpace(participationRef))
            {
                result.IsError = true;
                result.Message = "participationRef is required for a Participation grant.";
                return result;
            }
        }

        var grant = new ConsentGrant
        {
            GrantorAvatarId = grantorAvatarId,
            TenantId = tenantId,
            Scopes = normalizedScopes,
            GrantedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            Origin = origin,
            // ParticipationRef is only meaningful for a Participation grant; a
            // UserExplicit grant carries none even if one was supplied.
            ParticipationRef = origin == GrantOrigin.Participation
                ? participationRef!.Trim()
                : null,
        };

        var saved = await _grants.UpsertAsync(grant, ct);
        if (saved.IsError || saved.Result is null)
        {
            result.IsError = true;
            result.Message = saved.IsError ? saved.Message : "Failed to persist consent grant.";
            result.Exception = saved.Exception;
            return result;
        }

        var persisted = saved.Result;

        // Audit (AC10/L1): scope column carries the CSV of granted scopes.
        var auditError = await WriteAuditAsync(
            ConsentAuditAction.Granted,
            persisted.Id,
            persisted.TenantId,
            persisted.GrantorAvatarId,
            string.Join(",", persisted.Scopes),
            persisted.Origin == GrantOrigin.Participation
                ? $"origin=Participation; ref={persisted.ParticipationRef}"
                : "origin=UserExplicit",
            ct);
        if (auditError is not null)
        {
            // The grant IS persisted (the durable decision); surface the audit
            // failure for operator visibility rather than masking it.
            result.IsError = true;
            result.Message = auditError;
            return result;
        }

        // AC7: enqueue the consent.granted webhook event on the outbox in the SAME
        // logical flow as the grant Upsert (no dual-write; the delivery worker POSTs
        // later). Observe-only (AC8) — emit failure never affects grant validity.
        await _webhook.EmitAsync(ConsentWebhookEventType.Granted, persisted, ct);

        result.Result = persisted;
        result.Message = "Consent granted.";
        return result;
    }

    public Task<AZOAResult<ConsentGrant>> GrantParticipationAsync(
        Guid grantorAvatarId,
        Guid tenantId,
        string participationRef,
        IEnumerable<string> scopes,
        CancellationToken ct = default)
        // Delegates to GrantAsync with Participation origin so the value-scope
        // exclusion (H4) is enforced in exactly one place. The controller has
        // already authed this as the USER's token — a tenant API-key principal
        // alone can never reach here with a grantor it has no user-authenticated
        // assertion for.
        => GrantAsync(
            grantorAvatarId,
            tenantId,
            scopes,
            GrantOrigin.Participation,
            participationRef,
            expiresAt: null,
            ct);

    public async Task<AZOAResult<bool>> RevokeAsync(Guid grantorAvatarId, Guid grantId, CancellationToken ct = default)
    {
        var result = new AZOAResult<bool>();

        var loaded = await _grants.GetByIdAsync(grantId, ct);

        // IDOR / isolation (AC9): a missing grant AND a grant owned by another user
        // are reported IDENTICALLY as NOT_FOUND — a cross-user probe must be
        // indistinguishable from "no such grant", never Forbidden.
        if (loaded.IsError || loaded.Result is null
            || loaded.Result.GrantorAvatarId != grantorAvatarId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such consent grant.";
            return result;
        }

        var grant = loaded.Result;

        // Idempotent: an already-revoked grant stays revoked at its original time.
        if (grant.RevokedAt is null)
        {
            grant.RevokedAt = DateTime.UtcNow;

            var saved = await _grants.UpsertAsync(grant, ct);
            if (saved.IsError || saved.Result is null)
            {
                result.IsError = true;
                result.Message = saved.IsError ? saved.Message : "Failed to persist revocation.";
                result.Exception = saved.Exception;
                return result;
            }

            grant = saved.Result;

            var auditError = await WriteAuditAsync(
                ConsentAuditAction.Revoked,
                grant.Id,
                grant.TenantId,
                grant.GrantorAvatarId,
                string.Join(",", grant.Scopes),
                "revoked by user",
                ct);
            if (auditError is not null)
            {
                result.IsError = true;
                result.Message = auditError;
                return result;
            }

            // AC7: enqueue consent.revoked on the outbox in the same logical flow.
            await _webhook.EmitAsync(ConsentWebhookEventType.Revoked, grant, ct);
        }

        result.Result = true;
        result.Message = "Consent revoked.";
        return result;
    }

    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForUserAsync(Guid grantorAvatarId, CancellationToken ct = default)
    {
        // Store query is grantor-scoped — a cross-user probe yields an empty list,
        // never another user's grants (AC1).
        var listed = await _grants.ListByGrantorAsync(grantorAvatarId, ct);
        return Project(listed);
    }

    public async Task<AZOAResult<ConsentGrant>> GetForUserAsync(Guid grantorAvatarId, Guid grantId, CancellationToken ct = default)
    {
        var result = new AZOAResult<ConsentGrant>();

        var loaded = await _grants.GetByIdAsync(grantId, ct);

        // Same indistinguishable-from-miss rule as RevokeAsync (AC9).
        if (loaded.IsError || loaded.Result is null
            || loaded.Result.GrantorAvatarId != grantorAvatarId)
        {
            result.IsError = true;
            result.Message = TenantAuthorizationError.NotFound + "No such consent grant.";
            return result;
        }

        result.Result = loaded.Result;
        result.Message = "Success";
        return result;
    }

    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        // L2: store query is tenant-scoped — a tenant can never receive another
        // tenant's or a non-grantee's grants.
        var listed = await _grants.ListByTenantAsync(tenantId, ct);
        return Project(listed);
    }

    public async Task<AZOAResult<int>> RevokeByParticipationAsync(Guid tenantId, string participationRef, CancellationToken ct = default)
    {
        var result = new AZOAResult<int>();

        if (string.IsNullOrWhiteSpace(participationRef))
        {
            result.IsError = true;
            result.Message = "participationRef is required.";
            return result;
        }

        // L3 offboard: EXACT-match within the tenant's OWN grants only — the store
        // query is scoped to (tenantId, participationRef), so no cross-tenant ref
        // collision and no loose match is possible.
        var matched = await _grants.ListByTenantAndParticipationRefAsync(tenantId, participationRef.Trim(), ct);
        if (matched.IsError)
        {
            result.IsError = true;
            result.Message = matched.Message;
            result.Exception = matched.Exception;
            return result;
        }

        var grants = (matched.Result ?? Enumerable.Empty<ConsentGrant>()).ToList();
        var now = DateTime.UtcNow;
        var revokedCount = 0;

        foreach (var grant in grants)
        {
            // Skip already-revoked grants — idempotent offboarding, no double audit.
            if (grant.RevokedAt is not null)
                continue;

            grant.RevokedAt = now;

            var saved = await _grants.UpsertAsync(grant, ct);
            if (saved.IsError || saved.Result is null)
            {
                result.IsError = true;
                result.Message = saved.IsError ? saved.Message : "Failed to persist offboard revocation.";
                result.Exception = saved.Exception;
                return result;
            }

            var auditError = await WriteAuditAsync(
                ConsentAuditAction.Revoked,
                saved.Result.Id,
                saved.Result.TenantId,
                saved.Result.GrantorAvatarId,
                string.Join(",", saved.Result.Scopes),
                $"offboard revoke; ref={participationRef.Trim()}",
                ct);
            if (auditError is not null)
            {
                result.IsError = true;
                result.Message = auditError;
                return result;
            }

            // AC7: enqueue consent.revoked per offboarded grant.
            await _webhook.EmitAsync(ConsentWebhookEventType.Revoked, saved.Result, ct);

            revokedCount++;
        }

        result.Result = revokedCount;
        result.Message = $"Revoked {revokedCount} participation grant(s).";
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Trim, drop blanks, de-duplicate ordinally — the canonical scope set.</summary>
    private static List<string> NormalizeScopes(IEnumerable<string> scopes)
        => (scopes ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Appends one audit row. Returns null on success, or an error message when the
    /// audit write failed — the caller surfaces it WITHOUT undoing the already-
    /// persisted grant/revoke (the durable decision stands; the audit gap is the
    /// operator-visible failure).
    /// </summary>
    private async Task<string?> WriteAuditAsync(
        ConsentAuditAction action,
        Guid grantId,
        Guid tenantId,
        Guid avatarId,
        string scope,
        string? detail,
        CancellationToken ct)
    {
        var entry = new ConsentAuditEntry
        {
            Action = action,
            GrantId = grantId,
            TenantId = tenantId,
            AvatarId = avatarId,
            Scope = scope,
            Detail = detail,
            OccurredAt = DateTime.UtcNow,
        };

        var appended = await _audit.AppendAsync(entry, ct);
        if (appended.IsError || !appended.Result)
            return appended.IsError ? appended.Message : "Failed to write consent audit row.";

        return null;
    }

    /// <summary>Lifts an <see cref="IEnumerable{ConsentGrant}"/> store result into a
    /// manager result, materializing the list and never leaking null.</summary>
    private static AZOAResult<IEnumerable<ConsentGrant>> Project(AZOAResult<IEnumerable<ConsentGrant>> listed)
    {
        var result = new AZOAResult<IEnumerable<ConsentGrant>>();
        if (listed.IsError)
        {
            result.IsError = true;
            result.Message = listed.Message;
            result.Exception = listed.Exception;
            return result;
        }

        result.Result = (listed.Result ?? Enumerable.Empty<ConsentGrant>()).ToList();
        result.Message = "Success";
        return result;
    }
}
