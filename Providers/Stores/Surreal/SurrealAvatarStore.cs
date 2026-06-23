using Azoa.SurrealDb.Client;
using Azoa.SurrealDb.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using GeneratedAvatar = AZOA.WebAPI.Persistence.SurrealDb.Models.Avatar;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IAvatarStore"/>. Maps between the legacy
/// <see cref="Avatar"/> domain model and an inline POCO (no source-gen this round)
/// via private ToPoco / FromPoco helpers.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex,
/// no dashes). Dates are stored as DateTimeOffset with UTC kind applied.
/// </summary>
public sealed class SurrealAvatarStore : IAvatarStore
{
    private const string AvatarTable = "avatar";

    private readonly ISurrealExecutor _executor;

    public SurrealAvatarStore(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    // ── IAvatarStore ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IAvatar>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  AvatarTable)
                .WithParam("_id", ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IAvatar>
            {
                IsError = row == null,
                Message = row == null
                    ? $"Avatar not found (id: {id}). The avatar may have been deleted; if your session token references it, sign out and re-authenticate."
                    : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IAvatar>().CaptureException(ex, $"SurrealAvatarStore.GetByIdAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IAvatar>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(AvatarTable);
            var rows = await _executor.QueryAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IEnumerable<IAvatar>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IAvatar>>().CaptureException(ex, $"SurrealAvatarStore.GetAllAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IAvatar>> UpsertAsync(IAvatar avatar, CancellationToken ct = default)
    {
        try
        {
            if (avatar.Id == Guid.Empty)
                avatar.Id = Guid.NewGuid();

            var poco = ToPoco(avatar);

            var q    = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved = resp.GetValues<GeneratedAvatar>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : avatar;

            return new AZOAResult<IAvatar> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IAvatar>().CaptureException(ex, $"SurrealAvatarStore.UpsertAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  AvatarTable)
                .WithParam("_id", ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<GeneratedAvatar>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Avatar not found.", Result = false };

            var q = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  AvatarTable)
                .WithParam("_id", ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return new AZOAResult<bool>().CaptureException(ex, $"SurrealAvatarStore.DeleteAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IEnumerable<IAvatar>>> ListByOwnerTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            // Owner-scoped: only rows whose owner_tenant_id links to this tenant.
            // owner_tenant_id is a record<avatar> link, matched the same way
            // SurrealApiKeyStore.ListByAvatarAsync matches avatar_id.
            var q = SurrealQuery
                .Of("SELECT * FROM avatar WHERE owner_tenant_id = $_tenant ORDER BY created_date DESC")
                .WithParam("_tenant", SurrealLink.ToLink(AvatarTable, ToSurrealId(tenantId)));
            var rows = await _executor.QueryAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IEnumerable<IAvatar>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IEnumerable<IAvatar>>().CaptureException(ex, $"SurrealAvatarStore.ListByOwnerTenantAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IAvatar>> GetByTenantAndExternalUserAsync(Guid tenantId, string externalUserId, CancellationToken ct = default)
    {
        try
        {
            // Owner-scoped resolve. A miss returns Result == null with NO error so
            // the manager treats it as "create new" (idempotency), mirroring
            // ISTARStore.GetByNameAndAvatarAsync.
            var q = SurrealQuery
                .Of("SELECT * FROM avatar WHERE owner_tenant_id = $_tenant AND external_user_id = $_ext LIMIT 1")
                .WithParam("_tenant", SurrealLink.ToLink(AvatarTable, ToSurrealId(tenantId)))
                .WithParam("_ext", externalUserId);
            var row = await _executor.QuerySingleAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IAvatar>
            {
                Result  = row == null ? null : FromPoco(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IAvatar>().CaptureException(ex, $"SurrealAvatarStore.GetByTenantAndExternalUserAsync failed: {ex.Message}");
        }
    }

    public async Task<AZOAResult<IAvatar>> GetByAuthWalletAsync(string address, string chainType, CancellationToken ct = default)
    {
        try
        {
            // user-sovereign-identity AC2: resolve the avatar bound to EXACTLY this
            // (address, chainType) wallet-auth pair. A miss returns Result == null
            // with NO error so the manager treats it as "create new self-owned
            // avatar". Matching is on the wallet binding ONLY — never email/username.
            var q = SurrealQuery
                .Of("SELECT * FROM avatar WHERE auth_wallet_address = $_addr AND auth_wallet_chain_type = $_chain LIMIT 1")
                .WithParam("_addr", address)
                .WithParam("_chain", chainType);
            var row = await _executor.QuerySingleAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IAvatar>
            {
                Result  = row == null ? null : FromPoco(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return new AZOAResult<IAvatar>().CaptureException(ex, $"SurrealAvatarStore.GetByAuthWalletAsync failed: {ex.Message}");
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static string ToSurrealId(Guid id)
        => id.ToString("N").ToLowerInvariant();

    private static Guid FromSurrealId(string id)
        => Guid.ParseExact(id, "N");

    private static GeneratedAvatar ToPoco(IAvatar a) => new()
    {
        Id               = ToSurrealId(a.Id),
        Username         = a.Username,
        Email            = a.Email,
        PasswordHash     = a.PasswordHash,
        Title            = a.Title,
        FirstName        = a.FirstName,
        LastName         = a.LastName,
        CreatedDate      = new DateTimeOffset(
                               DateTime.SpecifyKind(a.CreatedDate, DateTimeKind.Utc)),
        LastBeamedInDate = a.LastBeamedInDate.HasValue
                               ? new DateTimeOffset(
                                     DateTime.SpecifyKind(a.LastBeamedInDate.Value, DateTimeKind.Utc))
                               : null,
        IsActive         = a.IsActive,
        IsVerified       = a.IsVerified,
        // owner_tenant_id is a record<avatar> link; encode the same way the
        // ApiKey store encodes avatar_id. null tenant => null link column.
        OwnerTenantId    = a.OwnerTenantId.HasValue
                               ? SurrealLink.ToLink(AvatarTable, ToSurrealId(a.OwnerTenantId.Value))
                               : null,
        ExternalUserId   = a.ExternalUserId,
        ExternalRef      = a.ExternalRef,
        AuthWalletAddress   = a.AuthWalletAddress,
        AuthWalletChainType = a.AuthWalletChainType,
        AuthNotBefore    = a.AuthNotBefore.HasValue
                               ? new DateTimeOffset(
                                     DateTime.SpecifyKind(a.AuthNotBefore.Value, DateTimeKind.Utc))
                               : null
    };

    private static Avatar FromPoco(GeneratedAvatar p) => new()
    {
        Id               = FromSurrealId(p.Id),
        Username         = p.Username,
        Email            = p.Email,
        PasswordHash     = p.PasswordHash,
        Title            = p.Title,
        FirstName        = p.FirstName,
        LastName         = p.LastName,
        CreatedDate      = p.CreatedDate.UtcDateTime,
        LastBeamedInDate = p.LastBeamedInDate?.UtcDateTime,
        IsActive         = p.IsActive,
        IsVerified       = p.IsVerified,
        OwnerTenantId    = string.IsNullOrEmpty(p.OwnerTenantId)
                               ? null
                               : Guid.ParseExact(SurrealLink.FromLink(p.OwnerTenantId)!, "N"),
        ExternalUserId   = p.ExternalUserId,
        ExternalRef      = p.ExternalRef,
        AuthWalletAddress   = p.AuthWalletAddress,
        AuthWalletChainType = p.AuthWalletChainType,
        AuthNotBefore    = p.AuthNotBefore?.UtcDateTime
    };

}
