using SurrealForge.Client;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;
using GeneratedUser = AZOA.WebAPI.Persistence.SurrealDb.Models.User;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Services.Auth;
using SurrealForge.Client.Idempotency;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealUserStore : IUserStore
{
    private const string UserTable = "user";

    private readonly ISurrealExecutor _executor;

    public SurrealUserStore(
        ISurrealExecutor executor,
        ILogger<SurrealUserStore>? logger = null)
    {
        _executor = executor;
    }


    /// <inheritdoc/>
    public async Task<AZOAResult<IUser>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var q = SurrealQuery<GeneratedUser>.Key(SurrealId.ToSurrealId(id));
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

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IUser>>> GetAllAsync(CancellationToken ct = default)
    {
        var q = SurrealQuery<GeneratedUser>.From();
        var rows = await _executor.QueryAsync<GeneratedUser>(q, ct);
        return new AZOAResult<IEnumerable<IUser>>
        {
            Result  = rows.Select(FromPoco).ToList(),
            Message = "Success"
        };

    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUser>> UpsertAsync(IUser user, CancellationToken ct = default)
    {
        if (user.Id == NodeOperatorIdentity.UserId)
            return AZOAResult<IUser>.Failure("The reserved node operator identity cannot be mutated through the user store.");
        if (string.Equals(user.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
            return AZOAResult<IUser>.Failure("The reserved node operator email cannot be assigned to a user.");

        if (user.Id == Guid.Empty)
            user.Id = Guid.NewGuid();
        var existing = await _executor.QuerySingleAsync<GeneratedUser>(SurrealQuery<GeneratedUser>.Key(SurrealId.ToSurrealId(user.Id)), ct);
        if (existing is null) return await CreateIfAbsentAsync(user, ct);
        if ((user.AuthWalletAddress is not null || user.AuthWalletChainType is not null
             || existing.AuthWalletAddress is not null || existing.AuthWalletChainType is not null)
            && !await WalletAuthIdentityGate.IsReadyAsync(_executor, ct)) return NotReady<IUser>();
        var poco = ToPoco(user);
        if (poco.AuthWalletAddress != existing.AuthWalletAddress || poco.AuthWalletChainType != existing.AuthWalletChainType
            || poco.OwnerTenantId != existing.OwnerTenantId || poco.AuthNotBefore != existing.AuthNotBefore)
            return AZOAResult<IUser>.FailureWithCode("Authentication and ownership changes require the dedicated identity flow.", AzoaErrorCodes.Conflict);
        var update = WalletUserAuthenticationCas.Match(existing)
            .Set(row => row.Username, poco.Username).Set(row => row.Email, poco.Email);
        update = poco.Title is null ? update.Unset(row => row.Title) : update.Set(row => row.Title, poco.Title);
        update = poco.FirstName is null ? update.Unset(row => row.FirstName) : update.Set(row => row.FirstName, poco.FirstName);
        update = poco.LastName is null ? update.Unset(row => row.LastName) : update.Set(row => row.LastName, poco.LastName);
        update = poco.LastBeamedInDate is null ? update.Unset(row => row.LastBeamedInDate) : update.Set(row => row.LastBeamedInDate, poco.LastBeamedInDate);
        update = poco.ExternalUserId is null ? update.Unset(row => row.ExternalUserId) : update.Set(row => row.ExternalUserId, poco.ExternalUserId);
        update = poco.ExternalRef is null ? update.Unset(row => row.ExternalRef) : update.Set(row => row.ExternalRef, poco.ExternalRef);
        var resp = await _executor.ExecuteAsync(update.Build(), ct);
        resp.EnsureAllOk();
        var saved = resp.GetValues<GeneratedUser>(0).FirstOrDefault();
        return saved is null ? AZOAResult<IUser>.FailureWithCode("Account authority changed concurrently.", AzoaErrorCodes.Conflict)
            : AZOAResult<IUser>.Success(FromPoco(saved));

    }

    /// <inheritdoc/>
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
            return AZOAResult<IUser>.Failure("The reserved node operator email cannot be assigned to a user.");
        }

        try
        {
            if ((user.AuthWalletAddress is not null || user.AuthWalletChainType is not null)
                && !await WalletAuthIdentityGate.IsReadyAsync(_executor, ct)) return NotReady<IUser>();
            var response = await _executor.ExecuteAsync(SurrealWriter.Create(ToPoco(user)), ct);
            response.EnsureAllOk();
            var saved = response.GetValues<GeneratedUser>(0).FirstOrDefault();
            return new AZOAResult<IUser>
            {
                Result = saved is null ? user : FromPoco(saved),
                Message = "Created."
            };
        }
        catch (SurrealStatementException ex) when (SurrealIdempotencyLedger.IsUniqueViolation(ex.Message))
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

            return AZOAResult<IUser>.FailureWithCode("The user or wallet identity already exists.", AzoaErrorCodes.Conflict);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == NodeOperatorIdentity.UserId)
            return AZOAResult<bool>.Failure("The reserved node operator identity cannot be deleted.");

        var existing = await _executor.QuerySingleAsync<GeneratedUser>(SurrealQuery<GeneratedUser>.Key(SurrealId.ToSurrealId(id)), ct);
        if (existing is null) return AZOAResult<bool>.Success(false);
        if (!await WalletAuthIdentityGate.IsReadyAsync(_executor, ct)) return NotReady<bool>();
        var response = await _executor.ExecuteAsync(SurrealWriter.DeleteOnly<GeneratedUser>(SurrealId.ToSurrealId(id))
            .Where(row => row.PasswordHash == existing.PasswordHash).Build(), ct);
        response.EnsureAllOk();
        return AZOAResult<bool>.Success(response.GetValues<GeneratedUser>(0).Any());
    }


    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<IUser>>> ListByOwnerTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenantLink = SurrealLink.ToLink(UserTable, SurrealId.ToSurrealId(tenantId));
        var q = SurrealQuery<GeneratedUser>.From().Where(row => row.OwnerTenantId == tenantLink)
            .OrderByDescending(row => row.CreatedDate);
        var rows = await _executor.QueryAsync<GeneratedUser>(q, ct);
        return new AZOAResult<IEnumerable<IUser>>
        {
            Result  = rows.Select(FromPoco).ToList(),
            Message = "Success"
        };

    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUser>> GetByTenantAndExternalUserAsync(Guid tenantId, string externalUserId, CancellationToken ct = default)
    {
        var tenantLink = SurrealLink.ToLink(UserTable, SurrealId.ToSurrealId(tenantId));
        var q = SurrealQuery<GeneratedUser>.From().Where(row => row.OwnerTenantId == tenantLink && row.ExternalUserId == externalUserId).Limit(1);
        var row = await _executor.QuerySingleAsync<GeneratedUser>(q, ct);
        return new AZOAResult<IUser>
        {
            Result  = row == null ? null : FromPoco(row),
            Message = "Success"
        };

    }

    /// <inheritdoc/>
    public Task<AZOAResult<IUser>> TryUpdateAuthenticationAsync(IUser user, string? expectedAddress, string? expectedChain,
        Guid? expectedTenant, DateTime? expectedAuthNotBefore, CancellationToken ct = default)
        => UpdateAuthenticationAsync(user, expectedAddress, expectedChain, expectedTenant, expectedAuthNotBefore, null, ct);

    /// <inheritdoc/>
    public Task<AZOAResult<IUser>> TryClaimAuthenticationAsync(IUser user, string? expectedAddress, string? expectedChain,
        Guid expectedTenant, DateTime? expectedAuthNotBefore, AZOA.WebAPI.Models.WalletAuthClaimToken invite,
        CancellationToken ct = default)
    {
        if (invite is null || invite.Id == Guid.Empty || string.IsNullOrWhiteSpace(invite.Token)
            || invite.TargetUserId != user.Id || invite.TenantId != expectedTenant || !invite.IsLiveAt(DateTime.UtcNow))
            return Task.FromResult(AZOAResult<IUser>.FailureWithCode("Invalid or expired claim token.", AzoaErrorCodes.Conflict));
        return UpdateAuthenticationAsync(user, expectedAddress, expectedChain, expectedTenant, expectedAuthNotBefore, invite, ct);
    }

    private async Task<AZOAResult<IUser>> UpdateAuthenticationAsync(IUser user, string? expectedAddress, string? expectedChain,
        Guid? expectedTenant, DateTime? expectedAuthNotBefore, AZOA.WebAPI.Models.WalletAuthClaimToken? invite, CancellationToken ct)
    {
        var claimOwnership = invite is not null;
        if (user.Id == NodeOperatorIdentity.UserId) return AZOAResult<IUser>.Failure("Reserved identity.");
        if (!await WalletAuthIdentityGate.IsReadyAsync(_executor, ct)) return NotReady<IUser>();
        var candidate = ToPoco(user);
        var tenantLink = expectedTenant.HasValue ? SurrealLink.ToLink(UserTable, SurrealId.ToSurrealId(expectedTenant.Value)) : null;
        DateTimeOffset? watermark = expectedAuthNotBefore.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(expectedAuthNotBefore.Value, DateTimeKind.Utc)) : null;
        var update = SurrealWriter.UpdateOnly<GeneratedUser>(candidate.Id).Where(row => row.IsActive);
        update = expectedAddress is null ? update.Where(row => row.AuthWalletAddress == null) : update.Where(row => row.AuthWalletAddress == expectedAddress);
        update = expectedChain is null ? update.Where(row => row.AuthWalletChainType == null) : update.Where(row => row.AuthWalletChainType == expectedChain);
        update = tenantLink is null ? update.Where(row => row.OwnerTenantId == null) : update.Where(row => row.OwnerTenantId == tenantLink);
        update = watermark is null ? update.Where(row => row.AuthNotBefore == null) : update.Where(row => row.AuthNotBefore == watermark);
        update = candidate.AuthWalletAddress is null ? update.Unset(row => row.AuthWalletAddress) : update.Set(row => row.AuthWalletAddress, candidate.AuthWalletAddress);
        update = candidate.AuthWalletChainType is null ? update.Unset(row => row.AuthWalletChainType) : update.Set(row => row.AuthWalletChainType, candidate.AuthWalletChainType);
        if (claimOwnership)
        {
            if (!expectedTenant.HasValue || candidate.AuthNotBefore is null || (watermark.HasValue && candidate.AuthNotBefore < watermark))
                return AZOAResult<IUser>.FailureWithCode("The user is no longer claimable.", AzoaErrorCodes.Conflict);
            update = update.Unset(row => row.OwnerTenantId).Set(row => row.PasswordHash, candidate.PasswordHash)
                .Set(row => row.AuthNotBefore, candidate.AuthNotBefore.Value);
        }
        try
        {
            SurrealQuery query = update.Build();
            if (claimOwnership)
            {
                var revocations = await new SurrealConsentLifecycleStore(_executor).ClaimRevocationsAsync(
                    user.Id, expectedTenant!.Value, candidate.AuthNotBefore!.Value, ct);
                var inviteExpiry = new DateTimeOffset(DateTime.SpecifyKind(invite!.ExpiresAt, DateTimeKind.Utc));
                var targetLink = SurrealLink.ToLink(UserTable, SurrealId.ToSurrealId(user.Id));
                // raw: invite consumption, ownership/authentication CAS and consent revocations commit together.
                var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"),
                    SurrealQuery.Of("IF $_claim_expiry <= time::now() { THROW 'CONSENT_CLAIM_CHANGED' }")
                        .WithParam("_claim_expiry", inviteExpiry),
                    SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthClaimToken>(SurrealId.ToSurrealId(invite.Id))
                        .Where(row => row.Token == invite.Token && row.TargetUserId == targetLink
                            && row.TenantId == tenantLink && row.ConsumedAt == null
                            && row.ExpiresAt == inviteExpiry)
                        .Set(row => row.ConsumedAt, candidate.AuthNotBefore.Value).Build(), "claim_invite", "_claim_invite"),
                    SurrealQuery.Of("IF $_claim_invite = NONE OR $_claim_invite = [] { THROW 'CONSENT_CLAIM_CHANGED' }"),
                    SurrealGeneratedStatementScope.Assign(update.Build(), "claim_user", "_claim_user"),
                    SurrealQuery.Of("IF $_claim_user = NONE OR $_claim_user = [] { THROW 'CONSENT_CLAIM_CHANGED' }") };
                statements.AddRange(revocations);
                statements.Add(SurrealQuery.Of("RETURN $_claim_user"));
                statements.Add(SurrealQuery.Of("COMMIT"));
                query = SurrealQuery.Combine(statements.ToArray());
            }
            var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await _executor.ExecuteAsync(query, ct); response.EnsureAllOk(); return response;
            }, ct);
            var saved = result.GetValues<GeneratedUser>(claimOwnership ? result.Statements.Count - 2 : 0).SingleOrDefault();
            return saved is null ? AZOAResult<IUser>.FailureWithCode("Authentication changed concurrently.", AzoaErrorCodes.Conflict)
                : AZOAResult<IUser>.Success(FromPoco(saved));
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return AZOAResult<IUser>.FailureWithCode("This wallet is already linked to another account.", AzoaErrorCodes.Conflict);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("CONSENT_CLAIM_CHANGED", StringComparison.Ordinal)
            || exception.Message.Contains("CONSENT_LIFECYCLE_CHANGED", StringComparison.Ordinal))
        {
            return AZOAResult<IUser>.FailureWithCode("Authentication or consent changed concurrently.", AzoaErrorCodes.Conflict);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUser>> GetByAuthWalletAsync(string address, string chainType, CancellationToken ct = default)
    {
        if (!await WalletAuthIdentityGate.IsReadyAsync(_executor, ct)) return NotReady<IUser>();
        if (!WalletAuthIdentity.TryCreate(address, chainType, out var canonicalAddress, out var canonicalChain))
            return AZOAResult<IUser>.Failure("Invalid wallet identity.");
        var row = await _executor.QuerySingleAsync<GeneratedUser>(SurrealQuery<GeneratedUser>.From()
            .Where(row => row.AuthWalletAddress == canonicalAddress && row.AuthWalletChainType == canonicalChain).Limit(1), ct);
        return new AZOAResult<IUser> { Result = row is null || SurrealId.FromSurrealId(row.Id) == NodeOperatorIdentity.UserId ? null : FromPoco(row) };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> AdvanceAuthenticationWatermarkAsync(Guid userId, DateTime notBefore, CancellationToken ct = default)
    {
        if (userId == NodeOperatorIdentity.UserId) return AZOAResult<bool>.Failure("Reserved identity.");
        var instant = new DateTimeOffset(DateTime.SpecifyKind(notBefore, DateTimeKind.Utc));
        var response = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<GeneratedUser>(SurrealId.ToSurrealId(userId))
            .Where(row => row.AuthNotBefore == null || row.AuthNotBefore < instant).Set(row => row.AuthNotBefore, instant).Build(), ct);
        response.EnsureAllOk();
        if (response.GetValues<GeneratedUser>(0).Any()) return AZOAResult<bool>.Success(true);
        var current = await _executor.QuerySingleAsync<GeneratedUser>(SurrealQuery<GeneratedUser>.Key(SurrealId.ToSurrealId(userId)), ct);
        return current?.AuthNotBefore >= instant ? AZOAResult<bool>.Success(true) : AZOAResult<bool>.Failure("User not found.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUser>> TryAssignDappRoleAsync(Guid userId, string expectedRole, string role, CancellationToken ct = default)
    {
        if (userId == NodeOperatorIdentity.UserId || !AzoaDappRoles.IsAssignableRole(role))
            return AZOAResult<IUser>.Failure("Invalid role assignment.");
        var response = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<GeneratedUser>(SurrealId.ToSurrealId(userId))
            .Where(row => row.DappRole == expectedRole).Set(row => row.DappRole, AzoaDappRoles.Normalize(role)).Build(), ct);
        response.EnsureAllOk();
        var saved = response.GetValues<GeneratedUser>(0).SingleOrDefault();
        return saved is null ? AZOAResult<IUser>.FailureWithCode("Role changed concurrently.", AzoaErrorCodes.Conflict)
            : AZOAResult<IUser>.Success(FromPoco(saved));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IUser>> TrySetActiveAsync(Guid userId, bool expectedActive, bool active, CancellationToken ct = default)
    {
        if (userId == NodeOperatorIdentity.UserId) return AZOAResult<IUser>.Failure("Reserved identity.");
        var response = await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<GeneratedUser>(SurrealId.ToSurrealId(userId))
            .Where(row => row.IsActive == expectedActive).Set(row => row.IsActive, active).Build(), ct);
        response.EnsureAllOk();
        var saved = response.GetValues<GeneratedUser>(0).SingleOrDefault();
        return saved is null ? AZOAResult<IUser>.FailureWithCode("Account activation changed concurrently.", AzoaErrorCodes.Conflict)
            : AZOAResult<IUser>.Success(FromPoco(saved));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<IUser>>> ListTenantPrincipalsPageAsync(
        int offset,
        int limit,
        string? search,
        CancellationToken ct = default)
    {
        // raw: expiring typed correlated-subquery gap; see AGENTS.md §Tenant-principal query waiver.
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

    private static AZOAResult<T> NotReady<T>() => AZOAResult<T>.FailureWithCode("Wallet identity migration is not ready.", AzoaErrorCodes.DependencyUnavailable);

    private static GeneratedUser ToPoco(IUser a)
    {
        string? address = null;
        string? chain = null;
        if (a.AuthWalletAddress is not null || a.AuthWalletChainType is not null)
        {
            if (!WalletAuthIdentity.TryCreate(a.AuthWalletAddress, a.AuthWalletChainType, out var canonicalAddress, out var canonicalChain))
                throw new ArgumentException("Invalid wallet authentication identity.");
            address = canonicalAddress;
            chain = canonicalChain;
        }
        return new GeneratedUser()
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
        AuthWalletAddress   = address,
        AuthWalletChainType = chain,
        AuthIdentityVersion = 1,
        AuthNotBefore    = a.AuthNotBefore.HasValue
                               ? new DateTimeOffset(
                                     DateTime.SpecifyKind(a.AuthNotBefore.Value, DateTimeKind.Utc))
                               : null
    };

    }

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
