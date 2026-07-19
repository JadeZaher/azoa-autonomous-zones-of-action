using SurrealForge.Client;
using SurrealForge.Client.Query;
using System.Diagnostics;
using System.Security.Cryptography;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string PublicStoreError =
        "AVATAR_STORE_UNAVAILABLE: Identity persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealAvatarStore> _logger;

    public SurrealAvatarStore(
        ISurrealExecutor executor,
        ILogger<SurrealAvatarStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealAvatarStore>.Instance;
    }

    // ── IAvatarStore ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IAvatar>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  AvatarTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IAvatar>
            {
                IsError = row == null,
                Code = row == null ? AzoaErrorCodes.NotFound : null,
                Message = row == null
                    ? $"Avatar not found (id: {id}). The avatar may have been deleted; if your session token references it, sign out and re-authenticate."
                    : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IAvatar>(ex, nameof(GetByIdAsync), id);
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
            return StoreFailure<IEnumerable<IAvatar>>(ex, nameof(GetAllAsync), "all");
        }
    }

    public async Task<AZOAResult<IAvatar>> UpsertAsync(IAvatar avatar, CancellationToken ct = default)
    {
        if (avatar.Id == NodeOperatorIdentity.AvatarId)
            return AZOAResult<IAvatar>.Failure("The reserved node operator identity cannot be mutated through the avatar store.");
        if (string.Equals(avatar.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
            return AZOAResult<IAvatar>.Failure("The reserved node operator email cannot be assigned to an avatar.");

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
            return StoreFailure<IAvatar>(ex, nameof(UpsertAsync), avatar.Id);
        }
    }

    public async Task<AZOAResult<IAvatar>> CreateIfAbsentAsync(IAvatar avatar, CancellationToken ct = default)
    {
        if (avatar.Id == Guid.Empty)
            return new AZOAResult<IAvatar> { IsError = true, Message = "A deterministic avatar id is required." };
        if (avatar.Id == NodeOperatorIdentity.AvatarId
            && (!string.Equals(avatar.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase)
                || !NodeOperatorIdentity.IsValidUsername(avatar.Username)
                || !NodeOperatorIdentity.IsStructurallyValidPasswordHash(avatar.PasswordHash)))
        {
            return AZOAResult<IAvatar>.Failure("The reserved node operator identity seed is invalid.");
        }
        if (avatar.Id != NodeOperatorIdentity.AvatarId
            && string.Equals(avatar.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<IAvatar>.Failure("The reserved node operator email cannot be assigned to an avatar.");
        }

        try
        {
            var response = await _executor.ExecuteAsync(SurrealWriter.Create(ToPoco(avatar)), ct);
            response.EnsureAllOk();
            var saved = response.GetValues<GeneratedAvatar>(0).FirstOrDefault();
            return new AZOAResult<IAvatar>
            {
                Result = saved is null ? avatar : FromPoco(saved),
                Message = "Created."
            };
        }
        catch (Exception ex)
        {
            // CREATE is the write boundary: a deterministic-id collision can only
            // be replayed by reading the existing row; it is never updated here.
            var existing = await GetByIdAsync(avatar.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return new AZOAResult<IAvatar>
                {
                    Result = existing.Result,
                    Message = "Already exists."
                };

            return StoreFailure<IAvatar>(ex, nameof(CreateIfAbsentAsync), avatar.Id);
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == NodeOperatorIdentity.AvatarId)
            return AZOAResult<bool>.Failure("The reserved node operator identity cannot be deleted.");

        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  AvatarTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<GeneratedAvatar>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "Avatar not found.", Result = false };

            var q = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  AvatarTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return StoreFailure<bool>(ex, nameof(DeleteAsync), id);
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
                .WithParam("_tenant", SurrealLink.ToLink(AvatarTable, SurrealId.ToSurrealId(tenantId)));
            var rows = await _executor.QueryAsync<GeneratedAvatar>(q, ct);
            return new AZOAResult<IEnumerable<IAvatar>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IEnumerable<IAvatar>>(ex, nameof(ListByOwnerTenantAsync), tenantId);
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
                .WithParam("_tenant", SurrealLink.ToLink(AvatarTable, SurrealId.ToSurrealId(tenantId)))
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
            return StoreFailure<IAvatar>(
                ex,
                nameof(GetByTenantAndExternalUserAsync),
                TenantSubjectCorrelation(tenantId, externalUserId));
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
                .Of("SELECT * FROM avatar WHERE id != type::record($_table, $_reserved) AND auth_wallet_address = $_addr AND auth_wallet_chain_type = $_chain LIMIT 1")
                .WithParam("_table", AvatarTable)
                .WithParam("_reserved", SurrealId.ToSurrealId(NodeOperatorIdentity.AvatarId))
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
            return StoreFailure<IAvatar>(ex, nameof(GetByAuthWalletAsync), chainType);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<IAvatar>>> ListTenantPrincipalsPageAsync(
        int offset,
        int limit,
        string? search,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_avatar_table) WHERE id != type::record($_avatar_table, $_reserved) AND is_active = true AND id INSIDE (SELECT VALUE avatar_id FROM type::table($_key_table) WHERE is_active = true AND revoked_at = NONE AND (expires_at = NONE OR expires_at > time::now()) AND scopes != NONE AND scopes CONTAINS $_scope) AND ($_search = '' OR string::lowercase(username) CONTAINS $_search OR string::lowercase(<string>id) CONTAINS $_search) ORDER BY username ASC, id ASC START $_offset LIMIT $_limit")
                .WithParam("_avatar_table", AvatarTable)
                .WithParam("_key_table", "api_key")
                .WithParam("_reserved", SurrealId.ToSurrealId(NodeOperatorIdentity.AvatarId))
                .WithParam("_scope", AzoaScopes.TenantProvision)
                .WithParam("_search", search?.Trim().ToLowerInvariant() ?? string.Empty)
                .WithParam("_offset", Math.Max(0, offset))
                .WithParam("_limit", Math.Clamp(limit, 1, 101));
            var rows = await _executor.QueryAsync<GeneratedAvatar>(query, ct);
            return AZOAResult<IReadOnlyList<IAvatar>>.Success(rows.Select(FromPoco).ToList());
        }
        catch (Exception ex)
        {
            return StoreFailure<IReadOnlyList<IAvatar>>(ex, nameof(ListTenantPrincipalsPageAsync), offset);
        }
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "Avatar persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(
            PublicStoreError,
            AzoaErrorCodes.DependencyUnavailable);
    }

    private static string TenantSubjectCorrelation(Guid tenantId, string externalUserId)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(externalUserId));
        return $"{tenantId:N}:{Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    // ── Mapping ───────────────────────────────────────────────────────────────



    private static GeneratedAvatar ToPoco(IAvatar a) => new()
    {
        Id               = SurrealId.ToSurrealId(a.Id),
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
        DappRole         = AZOA.WebAPI.Core.AzoaDappRoles.Normalize(a.DappRole),
        // owner_tenant_id is a record<avatar> link; encode the same way the
        // ApiKey store encodes avatar_id. null tenant => null link column.
        OwnerTenantId    = a.OwnerTenantId.HasValue
                               ? SurrealLink.ToLink(AvatarTable, SurrealId.ToSurrealId(a.OwnerTenantId.Value))
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
        Id               = SurrealId.FromSurrealId(p.Id),
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
        DappRole         = AZOA.WebAPI.Core.AzoaDappRoles.Normalize(p.DappRole),
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
