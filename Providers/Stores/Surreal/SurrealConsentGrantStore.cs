using System.Text.Json.Serialization;
using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IConsentGrantStore"/> (tenant-consent-delegation §1).
/// Mirrors the inline-POCO pattern of <see cref="SurrealApiKeyStore"/>: Guid("N")
/// lowercase-hex record ids, record-link columns for grantor/tenant, scopes stored
/// as a CSV string. Every list/lookup is scoped server-side so a cross-tenant /
/// cross-user probe surfaces nothing (AC9/L2/L3).
/// </summary>
public sealed class SurrealConsentGrantStore : IConsentGrantStore
{
    private const string Table = "consent_grant";

    private readonly ISurrealExecutor _executor;

    public SurrealConsentGrantStore(ISurrealExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<AZOAResult<ConsentGrant>> UpsertAsync(ConsentGrant grant, CancellationToken ct = default)
    {
        try
        {
            if (grant.Id == Guid.Empty) grant.Id = Guid.NewGuid();
            var poco = FromDomain(grant);
            var q = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();
            var saved = resp.GetValues<ConsentGrantPoco>(0).FirstOrDefault();
            return new AZOAResult<ConsentGrant>
            {
                Result = saved is not null ? ToDomain(saved) : grant,
                Message = "Saved."
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ConsentGrant>().CaptureException(ex, $"SurrealConsentGrantStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ConsentGrant>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t", Table)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<ConsentGrantPoco>(q, ct);
            return new AZOAResult<ConsentGrant>
            {
                Result = row is null ? null : ToDomain(row),
                Message = row is null ? "Consent grant not found." : "Success",
                IsError = row is null,
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ConsentGrant>().CaptureException(ex, $"SurrealConsentGrantStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByGrantorAsync(Guid grantorAvatarId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM consent_grant WHERE grantor_avatar_id = $_grantor ORDER BY granted_at DESC")
                .WithParam("_grantor", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(grantorAvatarId)));
            var rows = await _executor.QueryAsync<ConsentGrantPoco>(q, ct);
            return Ok(rows);
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<ConsentGrant>>().CaptureException(ex, $"SurrealConsentGrantStore.ListByGrantorAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM consent_grant WHERE tenant_id = $_tenant ORDER BY granted_at DESC")
                .WithParam("_tenant", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId)));
            var rows = await _executor.QueryAsync<ConsentGrantPoco>(q, ct);
            return Ok(rows);
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<ConsentGrant>>().CaptureException(ex, $"SurrealConsentGrantStore.ListByTenantAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<ConsentGrant>> FindCoveringGrantAsync(
        Guid grantorAvatarId, Guid tenantId, string scope, DateTime now, CancellationToken ct = default)
    {
        try
        {
            // AC4/AC5: the live lookup. Filter to (grantor, tenant) live (not revoked,
            // not expired at `now`) grants whose scope CSV contains `scope`. We filter
            // liveness in the query and re-confirm coverage in memory (the seam owns
            // the final Covers() word). A miss returns Result == null with NO error so
            // the gate fails closed.
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            var q = SurrealQuery
                .Of(@"SELECT * FROM consent_grant
                      WHERE grantor_avatar_id = $_grantor
                        AND tenant_id = $_tenant
                        AND revoked_at = NONE
                        AND (expires_at = NONE OR expires_at > $_now)")
                .WithParam("_grantor", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(grantorAvatarId)))
                .WithParam("_tenant", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId)))
                .WithParam("_now", nowUtc);
            var rows = await _executor.QueryAsync<ConsentGrantPoco>(q, ct);
            var covering = rows.Select(ToDomain).FirstOrDefault(g => g.Covers(scope, now));
            return new AZOAResult<ConsentGrant> { Result = covering, Message = "Success" };
        }
        catch (Exception ex)
        {
            return new AZOAResult<ConsentGrant>().CaptureException(ex, $"SurrealConsentGrantStore.FindCoveringGrantAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<ConsentGrant>>> ListByTenantAndParticipationRefAsync(
        Guid tenantId, string participationRef, CancellationToken ct = default)
    {
        try
        {
            // L3: tenant-scoped EXACT-match on participation_ref. Scoped to the
            // tenant's own grants only — no cross-tenant ref collision, no loose match.
            var q = SurrealQuery
                .Of("SELECT * FROM consent_grant WHERE tenant_id = $_tenant AND participation_ref = $_ref")
                .WithParam("_tenant", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId)))
                .WithParam("_ref", participationRef);
            var rows = await _executor.QueryAsync<ConsentGrantPoco>(q, ct);
            return Ok(rows);
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<ConsentGrant>>().CaptureException(ex, $"SurrealConsentGrantStore.ListByTenantAndParticipationRefAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<int>> RevokeAllByGrantorAsync(Guid grantorAvatarId, DateTime now, CancellationToken ct = default)
    {
        try
        {
            // AC3b: stamp revoked_at = $_now on EVERY currently-live grant this user made
            // (not already revoked). Scoped to the grantor's own grants only. Returning
            // the BEFORE set lets us count what was actually revoked. Idempotent: a
            // second call finds nothing live and revokes nothing.
            var nowUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
            var q = SurrealQuery
                .Of(@"UPDATE consent_grant
                      SET revoked_at = $_now
                      WHERE grantor_avatar_id = $_grantor
                        AND revoked_at = NONE
                      RETURN BEFORE")
                .WithParam("_grantor", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(grantorAvatarId)))
                .WithParam("_now", nowUtc);
            var rows = await _executor.QueryAsync<ConsentGrantPoco>(q, ct);
            return new AZOAResult<int> { Result = rows.Count, Message = "Revoked." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<int>().CaptureException(ex, $"SurrealConsentGrantStore.RevokeAllByGrantorAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static AZOAResult<IEnumerable<ConsentGrant>> Ok(IReadOnlyList<ConsentGrantPoco> rows)
        => new() { Result = rows.Select(ToDomain).ToList(), Message = "Success" };


    private static ConsentGrantPoco FromDomain(ConsentGrant g) => new()
    {
        Id               = SurrealId.ToSurrealId(g.Id),
        GrantorAvatarId  = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(g.GrantorAvatarId)) ?? string.Empty,
        TenantId         = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(g.TenantId)) ?? string.Empty,
        Scopes           = string.Join(',', g.Scopes),
        Origin           = g.Origin.ToString(),
        ParticipationRef = g.ParticipationRef,
        GrantedAt        = new DateTimeOffset(DateTime.SpecifyKind(g.GrantedAt, DateTimeKind.Utc)),
        ExpiresAt        = g.ExpiresAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(g.ExpiresAt.Value, DateTimeKind.Utc)) : null,
        RevokedAt        = g.RevokedAt.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(g.RevokedAt.Value, DateTimeKind.Utc)) : null,
    };

    private static ConsentGrant ToDomain(ConsentGrantPoco p) => new()
    {
        Id               = SurrealId.FromSurrealId(p.Id),
        GrantorAvatarId  = SurrealId.FromSurrealId(SurrealLink.FromLink(p.GrantorAvatarId)!),
        TenantId         = SurrealId.FromSurrealId(SurrealLink.FromLink(p.TenantId)!),
        Scopes           = string.IsNullOrWhiteSpace(p.Scopes)
                           ? new List<string>()
                           : p.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Origin           = Enum.TryParse<GrantOrigin>(p.Origin, ignoreCase: true, out var o) ? o : GrantOrigin.UserExplicit,
        ParticipationRef = p.ParticipationRef,
        GrantedAt        = p.GrantedAt.UtcDateTime,
        ExpiresAt        = p.ExpiresAt?.UtcDateTime,
        RevokedAt        = p.RevokedAt?.UtcDateTime,
    };

    // ── POCO (private; inline until source-gen catches up) ────────────────────

    private sealed class ConsentGrantPoco : Azoa.SurrealDb.Client.ISurrealRecord
    {
        public string SchemaName => Table;

        [JsonPropertyName("id")]                 public string Id { get; set; } = string.Empty;
        [JsonPropertyName("grantor_avatar_id")]  public string GrantorAvatarId { get; set; } = string.Empty;
        [JsonPropertyName("tenant_id")]          public string TenantId { get; set; } = string.Empty;
        [JsonPropertyName("scopes")]             public string Scopes { get; set; } = string.Empty;
        [JsonPropertyName("origin")]             public string Origin { get; set; } = "UserExplicit";
        [JsonPropertyName("participation_ref")]  public string? ParticipationRef { get; set; }
        [JsonPropertyName("granted_at")]         public DateTimeOffset GrantedAt { get; set; }
        [JsonPropertyName("expires_at")]         public DateTimeOffset? ExpiresAt { get; set; }
        [JsonPropertyName("revoked_at")]         public DateTimeOffset? RevokedAt { get; set; }
    }
}
