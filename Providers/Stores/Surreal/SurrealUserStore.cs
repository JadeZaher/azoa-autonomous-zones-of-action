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
using GeneratedUser = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IUserStore"/>. Maps between the legacy
/// <see cref="User"/> domain model and an inline POCO (no source-gen this round)
/// via private ToPoco / FromPoco helpers.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex,
/// no dashes). Dates are stored as DateTimeOffset with UTC kind applied.
/// </summary>
public sealed class SurrealUserStore : IUserStore
{
    private const string UserTable = "user";
    private const string PublicStoreError =
        "USER_STORE_UNAVAILABLE: Identity persistence is temporarily unavailable.";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealUserStore> _logger;

    public SurrealUserStore(
        ISurrealExecutor executor,
        ILogger<SurrealUserStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealUserStore>.Instance;
    }

    // ── IUserStore ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<IUser>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  UserTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<GeneratedUser>(q, ct);
            return new AZOAResult<IUser>
            {
                IsError = row == null,
                Code = row == null ? AzoaErrorCodes.NotFound : null,
                Message = row == null
                    ? $"User not found (id: {id}). The user may have been deleted; if your session token references it, sign out and re-authenticate."
                    : "Success",
                Result  = row == null ? null : FromPoco(row)
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IUser>(ex, nameof(GetByIdAsync), id);
        }
    }

    public async Task<AZOAResult<IEnumerable<IUser>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectAll(UserTable);
            var rows = await _executor.QueryAsync<GeneratedUser>(q, ct);
            return new AZOAResult<IEnumerable<IUser>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IEnumerable<IUser>>(ex, nameof(GetAllAsync), "all");
        }
    }

    public async Task<AZOAResult<IUser>> UpsertAsync(IUser user, CancellationToken ct = default)
    {
        if (user.Id == NodeOperatorIdentity.UserId)
            return AZOAResult<IUser>.Failure("The reserved node operator identity cannot be mutated through the user store.");
        if (string.Equals(user.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
            return AZOAResult<IUser>.Failure("The reserved node operator email cannot be assigned to an user.");

        try
        {
            if (user.Id == Guid.Empty)
                user.Id = Guid.NewGuid();

            var poco = ToPoco(user);

            var q    = SurrealWriter.Upsert(poco);
            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved = resp.GetValues<GeneratedUser>(0).FirstOrDefault();
            var result = saved is not null ? FromPoco(saved) : user;

            return new AZOAResult<IUser> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return StoreFailure<IUser>(ex, nameof(UpsertAsync), user.Id);
        }
    }

    public async Task<AZOAResult<IUser>> CreateIfAbsentAsync(IUser user, CancellationToken ct = default)
    {
        if (user.Id == Guid.Empty)
            return new AZOAResult<IUser> { IsError = true, Message = "A deterministic user id is required." };
        if (user.Id == NodeOperatorIdentity.UserId
            && (!string.Equals(user.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase)
                || !NodeOperatorIdentity.IsValidUsername(user.Username)
                || !NodeOperatorIdentity.IsStructurallyValidPasswordHash(user.PasswordHash)))
        {
            return AZOAResult<IUser>.Failure("The reserved node operator identity seed is invalid.");
        }
        if (user.Id != NodeOperatorIdentity.UserId
            && string.Equals(user.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
        {
            return AZOAResult<IUser>.Failure("The reserved node operator email cannot be assigned to an user.");
        }

        try
        {
            var response = await _executor.ExecuteAsync(SurrealWriter.Create(ToPoco(user)), ct);
            response.EnsureAllOk();
            var saved = response.GetValues<GeneratedUser>(0).FirstOrDefault();
            return new AZOAResult<IUser>
            {
                Result = saved is null ? user : FromPoco(saved),
                Message = "Created."
            };
        }
        catch (Exception ex)
        {
            // CREATE is the write boundary: a deterministic-id collision can only
            // be replayed by reading the existing row; it is never updated here.
            var existing = await GetByIdAsync(user.Id, ct);
            if (!existing.IsError && existing.Result is not null)
                return new AZOAResult<IUser>
                {
                    Result = existing.Result,
                    Message = "Already exists."
                };

            return StoreFailure<IUser>(ex, nameof(CreateIfAbsentAsync), user.Id);
        }
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == NodeOperatorIdentity.UserId)
            return AZOAResult<bool>.Failure("The reserved node operator identity cannot be deleted.");

        try
        {
            // Check existence first (matches the prior EF read-before-update contract).
            var checkQ = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  UserTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            var existing = await _executor.QuerySingleAsync<GeneratedUser>(checkQ, ct);
            if (existing == null)
                return new AZOAResult<bool> { IsError = true, Message = "User not found.", Result = false };

            var q = SurrealQuery
                .Of("DELETE type::record($_t, $_id)")
                .WithParam("_t",  UserTable)
                .WithParam("_id", SurrealId.ToSurrealId(id));
            await _executor.ExecuteAsync(q, ct);

            return new AZOAResult<bool> { Result = true, Message = "Deleted." };
        }
        catch (Exception ex)
        {
            return StoreFailure<bool>(ex, nameof(DeleteAsync), id);
        }
    }

    public async Task<AZOAResult<IEnumerable<IUser>>> ListByOwnerTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            // Owner-scoped: only rows whose owner_tenant_id links to this tenant.
            // owner_tenant_id is a record<user> link, matched the same way
            // SurrealApiKeyStore.ListByUserAsync matches user_id.
            var q = SurrealQuery
                .Of("SELECT * FROM user WHERE owner_tenant_id = $_tenant ORDER BY created_date DESC")
                .WithParam("_tenant", SurrealRecordParam.Of(UserTable, SurrealId.ToSurrealId(tenantId)));
            var rows = await _executor.QueryAsync<GeneratedUser>(q, ct);
            return new AZOAResult<IEnumerable<IUser>>
            {
                Result  = rows.Select(FromPoco).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IEnumerable<IUser>>(ex, nameof(ListByOwnerTenantAsync), tenantId);
        }
    }

    public async Task<AZOAResult<IUser>> GetByTenantAndExternalUserAsync(Guid tenantId, string externalUserId, CancellationToken ct = default)
    {
        try
        {
            // Owner-scoped resolve. A miss returns Result == null with NO error so
            // the manager treats it as "create new" (idempotency), mirroring
            // ISTARStore.GetByNameAndUserAsync.
            var q = SurrealQuery
                .Of("SELECT * FROM user WHERE owner_tenant_id = $_tenant AND external_user_id = $_ext LIMIT 1")
                .WithParam("_tenant", SurrealRecordParam.Of(UserTable, SurrealId.ToSurrealId(tenantId)))
                .WithParam("_ext", externalUserId);
            var row = await _executor.QuerySingleAsync<GeneratedUser>(q, ct);
            return new AZOAResult<IUser>
            {
                Result  = row == null ? null : FromPoco(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IUser>(
                ex,
                nameof(GetByTenantAndExternalUserAsync),
                TenantSubjectCorrelation(tenantId, externalUserId));
        }
    }

    public async Task<AZOAResult<IUser>> GetByAuthWalletAsync(string address, string chainType, CancellationToken ct = default)
    {
        try
        {
            // user-sovereign-identity AC2: resolve the user bound to EXACTLY this
            // (address, chainType) wallet-auth pair. A miss returns Result == null
            // with NO error so the manager treats it as "create new self-owned
            // user". Matching is on the wallet binding ONLY — never email/username.
            var q = SurrealQuery
                .Of("SELECT * FROM user WHERE id != type::record($_table, $_reserved) AND auth_wallet_address = $_addr AND auth_wallet_chain_type = $_chain LIMIT 1")
                .WithParam("_table", UserTable)
                .WithParam("_reserved", SurrealId.ToSurrealId(NodeOperatorIdentity.UserId))
                .WithParam("_addr", address)
                .WithParam("_chain", chainType);
            var row = await _executor.QuerySingleAsync<GeneratedUser>(q, ct);
            return new AZOAResult<IUser>
            {
                Result  = row == null ? null : FromPoco(row),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IUser>(ex, nameof(GetByAuthWalletAsync), chainType);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<IUser>>> ListTenantPrincipalsPageAsync(
        int offset,
        int limit,
        string? search,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_user_table) WHERE id != type::record($_user_table, $_reserved) AND is_active = true AND id INSIDE (SELECT VALUE user_id FROM type::table($_key_table) WHERE is_active = true AND revoked_at = NONE AND (expires_at = NONE OR expires_at > time::now()) AND scopes != NONE AND scopes CONTAINS $_scope) AND ($_search = '' OR string::lowercase(username) CONTAINS $_search OR string::lowercase(<string>id) CONTAINS $_search) ORDER BY username ASC, id ASC START $_offset LIMIT $_limit")
                .WithParam("_user_table", UserTable)
                .WithParam("_key_table", "api_key")
                .WithParam("_reserved", SurrealId.ToSurrealId(NodeOperatorIdentity.UserId))
                .WithParam("_scope", AzoaScopes.TenantProvision)
                .WithParam("_search", search?.Trim().ToLowerInvariant() ?? string.Empty)
                .WithParam("_offset", Math.Max(0, offset))
                .WithParam("_limit", Math.Clamp(limit, 1, 101));
            var rows = await _executor.QueryAsync<GeneratedUser>(query, ct);
            return AZOAResult<IReadOnlyList<IUser>>.Success(rows.Select(FromPoco).ToList());
        }
        catch (Exception ex)
        {
            return StoreFailure<IReadOnlyList<IUser>>(ex, nameof(ListTenantPrincipalsPageAsync), offset);
        }
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "User persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
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



    private static GeneratedUser ToPoco(IUser a) => new()
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
        // owner_tenant_id is a record<user> link; encode the same way the
        // ApiKey store encodes user_id. null tenant => null link column.
        OwnerTenantId    = a.OwnerTenantId.HasValue
                               ? SurrealLink.ToLink(UserTable, SurrealId.ToSurrealId(a.OwnerTenantId.Value))
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

    private static User FromPoco(GeneratedUser p) => new()
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
